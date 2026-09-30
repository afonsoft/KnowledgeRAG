using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Services;
using KnowledgeHub.McpEngine.Activity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// SPEC-20260926-mcp-sdk-alignment RF-003: HITL approval for write tools called
/// via <c>tools/call</c>, expressed with the spec's multi round-trip request
/// (MRTR) mechanism — <c>resultType: input_required</c> + <c>elicitation/create</c>
/// + opaque <c>requestState</c>, retried with <c>inputResponses</c>.
/// Only fires when the client actually declared elicitation support (stateful
/// handshake caps or the per-request <c>_meta</c> capabilities of the
/// 2026-07-28 era); every other caller keeps today's behavior.
/// </summary>
public static class MrtrApproval
{
    /// <summary>Key of the elicitation entry in <c>inputRequests</c>/<c>inputResponses</c>.</summary>
    public const string InputKey = "approval";

    private const string ProtectorPurpose = "mcp-mrtr-approval";
    private const string ClientCapsKey = "io.modelcontextprotocol/clientCapabilities";

    /// <summary>Same policy as the agent loop: non-readonly tools matched by
    /// <c>Agent:RequireApprovalFor</c> ("*" gates all of them) need an approval.</summary>
    public static bool RequiresApproval(IServiceProvider services, CatalogTool tool)
    {
        if (tool.ReadOnly)
            return false;
        var options = services.GetService<IOptions<Agent.AgentOptions>>()?.Value;
        var list = options?.RequireApprovalFor ?? ["*"];
        return list.Contains("*")
            || list.Contains(tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>MRTR is only possible when the client can fulfill elicitations —
    /// stateful clients via negotiated <see cref="ClientCapabilities"/>, stateless
    /// (2026-07-28 era) via per-request <c>_meta</c> capabilities.</summary>
    public static bool ClientSupportsElicitation(RequestContext<CallToolRequestParams> ctx)
    {
        if (ctx.Server?.ClientCapabilities?.Elicitation is not null)
            return true;

        if (ctx.Params?.Meta is { } meta
            && meta.TryGetPropertyValue(ClientCapsKey, out var caps)
            && caps is JsonObject o
            && o.ContainsKey("elicitation"))
            return true;

        return false;
    }

    /// <summary>Client declared an extension (e.g. <c>io.modelcontextprotocol/tasks</c>)
    /// — stateful via negotiated <see cref="ClientCapabilities.Extensions"/>,
    /// stateless (2026-07-28) via per-request <c>_meta</c> capabilities.</summary>
    public static bool ClientDeclaredExtension(RequestContext<CallToolRequestParams> ctx, string extensionId)
    {
        if (ctx.Server?.ClientCapabilities?.Extensions?.ContainsKey(extensionId) == true)
            return true;

        if (ctx.Params?.Meta is { } meta
            && meta.TryGetPropertyValue(ClientCapsKey, out var caps)
            && caps is JsonObject o
            && o["extensions"] is JsonObject exts
            && exts.ContainsKey(extensionId))
            return true;

        return false;
    }

    /// <summary>A retry carries the server-issued <c>requestState</c> and the
    /// client's <c>inputResponses</c>.</summary>
    public static bool IsRetry(RequestContext<CallToolRequestParams> ctx) =>
        ctx.Params is { RequestState.Length: > 0, InputResponses.Count: > 0 };

    /// <summary>Creates the pending <see cref="ToolApproval"/> (audited in the UI
    /// feed) and returns the exception the SDK translates into
    /// <c>resultType: "input_required"</c>.</summary>
    public static async Task<InputRequiredException> CreateAsync(
        RequestContext<CallToolRequestParams> ctx, CatalogTool tool, CancellationToken ct)
    {
        var services = ctx.Services!;
        var db = services.GetRequiredService<KnowledgeHubDbContext>();
        var feed = services.GetService<IMcpActivityFeed>();

        var rawArgs = ctx.Params?.Arguments is { } args
            ? JsonSerializer.SerializeToElement(args, JsonSerializerOptions.Web)
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(), JsonSerializerOptions.Web);

        var approval = new ToolApproval
        {
            ToolName = tool.Name,
            ArgumentsJson = ApprovalService.MaskSensitive(rawArgs).GetRawText(),
            RequestedBy = "mcp",
            Status = "pending",
            // The unmasked args are execution state, not display data.
            StateJson = JsonSerializer.Serialize(new MrtrCall(tool.Name, rawArgs), JsonSerializerOptions.Web)
        };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync(ct);

        feed?.Record(new McpActivityEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = McpActivityKind.ApprovalRequested,
            Transport = "mcp",
            Method = "tools/call",
            ToolName = tool.Name
        });

        var protector = services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ProtectorPurpose);
        var state = protector.Protect(
            JsonSerializer.Serialize(new { approvalId = approval.Id }));

        return new InputRequiredException(new InputRequiredResult
        {
            RequestState = state,
            InputRequests = new Dictionary<string, InputRequest>
            {
                [InputKey] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Mode = "form",
                    Message = $"Tool '{tool.Name}' requires approval. " +
                              $"Arguments: {approval.ArgumentsJson}",
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        {
                            ["confirm"] = new ElicitRequestParams.BooleanSchema
                            {
                                Type = "boolean",
                                Title = "Approve",
                                Description = $"Allow '{tool.Name}' to execute"
                            }
                        },
                        Required = ["confirm"]
                    }
                })
            }
        });
    }

    /// <summary>Resolves a retry: validates the opaque state, applies the
    /// elicitation outcome to the pending approval, and — on accept — executes
    /// the stored call. Denied/cancelled/invalid retries return an isError
    /// result instead of executing.</summary>
    public static async Task<CallToolResult> ResumeAsync(
        RequestContext<CallToolRequestParams> ctx, CatalogTool tool, CancellationToken ct)
    {
        var services = ctx.Services!;
        var protector = services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ProtectorPurpose);

        Guid approvalId;
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(
                protector.Unprotect(ctx.Params.RequestState!));
            approvalId = payload.GetProperty("approvalId").GetGuid();
        }
        catch (Exception)
        {
            return Error($"invalid requestState — the pending input request can no longer be resolved");
        }

        var db = services.GetRequiredService<KnowledgeHubDbContext>();
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct);
        var timeout = services.GetService<IOptions<Agent.AgentOptions>>()?.Value.ApprovalTimeoutMinutes ?? 30;
        if (approval is null || approval.Status != "pending" || approval.ResumedAt is not null
            || approval.CreatedAt + TimeSpan.FromMinutes(timeout) < DateTimeOffset.UtcNow)
            return Error("the pending input request expired or was already resolved — call the tool again");

        if (approval.ToolName != tool.Name)
            return Error("requestState does not match this tool");

        ctx.Params.InputResponses!.TryGetValue(InputKey, out var response);
        var action = ReadAction(response);

        approval.ResolvedAt = DateTimeOffset.UtcNow;
        approval.ResumedAt = DateTimeOffset.UtcNow;

        if (action != "accept")
        {
            approval.Status = "denied";
            await db.SaveChangesAsync(ct);
            return Error($"tool '{tool.Name}' was not approved ({action ?? "missing input"})");
        }

        approval.Status = "approved";
        await db.SaveChangesAsync(ct);

        var stored = JsonSerializer.Deserialize<MrtrCall>(approval.StateJson!, JsonSerializerOptions.Web);
        var result = await tool.Handler(new ToolCallContext
        {
            Services = services,
            Arguments = stored?.Args is { ValueKind: JsonValueKind.Object } a
                ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : null
        }, ct);

        return result;
    }

    private static string? ReadAction(InputResponse? response)
    {
        if (response is null)
            return null;
        try
        {
            var elicit = response.RawValue.Deserialize<ElicitResult>(JsonSerializerOptions.Web);
            return elicit?.Action;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CallToolResult Error(string message) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }]
        };

    private sealed record MrtrCall(string Tool, JsonElement Args);
}
