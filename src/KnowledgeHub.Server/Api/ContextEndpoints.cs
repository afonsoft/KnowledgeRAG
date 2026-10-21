using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Context;
using KnowledgeHub.Server.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// REST mirror of the <c>context_*</c> MCP tools
/// (SPEC-20261004-context-snapshots RF-009/API contract): same handlers via
/// thin adapters so both surfaces share validation, cache and persistence.
/// ProblemDetails error mapping keys off the <c>code:</c> prefix convention
/// used by <see cref="McpProtocolException"/> messages in this feature.
/// </summary>
public static class ContextEndpoints
{
    public static RouteGroupBuilder MapContextApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/context");

        group.MapPost("/snapshots", SaveSnapshot);
        group.MapGet("/snapshots", ListSnapshots);
        group.MapGet("/snapshots/latest", LatestSnapshot);
        group.MapGet("/snapshots/{version:int}", SnapshotByVersion);
        // SPEC-20261021-context-rest-contract-fixes RF-4: compact may trigger
        // LLM summarization — own bucket mirroring the MCP 'context' partition
        // instead of the loose general policy.
        group.MapPost("/compact", Compact).RequireRateLimiting("context");
        return group;
    }

    /// <summary>Builds the transport-neutral call context for the handler.</summary>
    private static ToolCallContext CallContext(HttpContext http, JsonObject? body)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body is not null)
            foreach (var prop in body)
                if (prop.Value is not null)
                    args[prop.Key] = JsonSerializer.SerializeToElement(prop.Value);
        return new ToolCallContext
        {
            Services = http.RequestServices,
            Arguments = args,
            Transport = "rest"
        };
    }

    private sealed record RestResult(IResult Result, JsonObject? Payload);

    /// <summary>Invokes a catalog tool handler and maps the outcome to HTTP.
    /// <c>McpProtocolException</c> code prefixes map to the contract table.</summary>
    private static async Task<RestResult> InvokeAsync(
        HttpContext http, string toolName, JsonObject? body, CancellationToken ct,
        bool created = false)
    {
        var catalog = http.RequestServices.GetRequiredService<IDynamicToolCatalog>();
        var tool = (await catalog.GetToolsAsync(http.RequestServices, ct))
            .FirstOrDefault(t => t.Name == toolName);
        if (tool is null)
            return new RestResult(Results.Problem(
                statusCode: 500, title: "tool_unavailable",
                detail: $"tool '{toolName}' is not registered"), null);
        try
        {
            var result = await tool.Handler(CallContext(http, body), ct);
            var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .FirstOrDefault()?.Text ?? "";
            if (result.IsError == true)
                return new RestResult(ProblemFor(text), null);
            var payload = result.StructuredContent is { } el
                ? el.Deserialize<JsonObject>() ?? new JsonObject()
                : new JsonObject();
            // SPEC-20261021 RF-2: resource creation returns 201 + Location.
            if (created && payload["version"] is not null && payload["repo"] is not null)
            {
                var location = $"/api/context/snapshots/{payload["version"]?.GetValue<int>()}"
                    + $"?repo={Uri.EscapeDataString(payload["repo"]!.GetValue<string>()!)}";
                return new RestResult(Results.Created(location, payload), payload);
            }
            return new RestResult(Results.Ok(payload), payload);
        }
        catch (McpProtocolException ex)
        {
            return new RestResult(ProblemFor(ex.Message), null);
        }
    }

    private static IResult ProblemFor(string message)
    {
        var (status, code) = message switch
        {
            var m when m.StartsWith("invalid_repo", StringComparison.Ordinal) => (400, "invalid_repo"),
            var m when m.StartsWith("secret_detected", StringComparison.Ordinal) => (400, "secret_detected"),
            var m when m.StartsWith("payload_too_large", StringComparison.Ordinal) => (413, "payload_too_large"),
            var m when m.StartsWith("context_not_found", StringComparison.Ordinal) => (404, "context_not_found"),
            var m when m.StartsWith("repo_forbidden", StringComparison.Ordinal) => (403, "repo_forbidden"),
            _ => (400, "invalid_payload")
        };
        return Results.Problem(statusCode: status, title: code, detail: message);
    }

    private static async Task<JsonObject?> BodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is null or 0)
            return null;
        return await http.Request.ReadFromJsonAsync<JsonObject>(ct);
    }

    private static async Task<IResult> SaveSnapshot(HttpContext http, CancellationToken ct)
    {
        var r = await InvokeAsync(http, "context_save", await BodyAsync(http, ct), ct, created: true);
        return r.Result;
    }

    private static async Task<IResult> Compact(HttpContext http, CancellationToken ct)
    {
        var r = await InvokeAsync(http, "context_compact", await BodyAsync(http, ct), ct);
        return r.Result;
    }

    private static async Task<IResult> LatestSnapshot(HttpContext http, CancellationToken ct)
    {
        var body = new JsonObject();
        foreach (var key in new[] { "repo", "strategy", "label" })
            if (http.Request.Query[key] is { } v && !string.IsNullOrEmpty(v))
                body[key] = v.ToString();
        if (int.TryParse(http.Request.Query["tokenBudget"], out var tb))
            body["tokenBudget"] = tb;
        var r = await InvokeAsync(http, "context_recovery", body, ct);
        return r.Result;
    }

    private static async Task<IResult> SnapshotByVersion(
        HttpContext http, int version, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["repo"] = http.Request.Query["repo"].ToString(),
            ["version"] = version
        };
        if (http.Request.Query["strategy"] is { } s && !string.IsNullOrEmpty(s))
            body["strategy"] = s.ToString();
        if (int.TryParse(http.Request.Query["tokenBudget"], out var tb))
            body["tokenBudget"] = tb;
        var r = await InvokeAsync(http, "context_recovery", body, ct);
        return r.Result;
    }

    private static async Task<IResult> ListSnapshots(HttpContext http, CancellationToken ct)
    {
        if (!RepoScope.TryParse(http.Request.Query["repo"], out var repo))
            return ProblemFor("invalid_repo: 'repo' query parameter is required (owner/name)");
        var take = int.TryParse(http.Request.Query["take"], out var t) ? Math.Clamp(t, 1, 200) : 50;
        var service = http.RequestServices.GetRequiredService<ContextSnapshotService>();
        var list = await service.ListAsync(repo, take, ct);
        return Results.Ok(list.Select(m => new
        {
            id = m.Id,
            version = m.Version,
            label = m.Label,
            tokenEstimate = m.TokenEstimate,
            payloadBytes = m.PayloadBytes,
            compacted = m.Compacted,
            strategy = m.Strategy,
            source = m.Source,
            createdAt = m.CreatedAt
        }));
    }
}
