using System.Diagnostics;
using System.Text.Json;
using A2A;
using KnowledgeHub.Server.Audit.Evidence;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using A2ARole = A2A.Role;

namespace KnowledgeHub.Server.A2A;

/// <summary>
/// SPEC-20260929-a2a-server-interop RF-001/RF-002/RF-003: maps an A2A message
/// onto the live MCP tool catalog. The catalog call goes through the caller's
/// request scope (<see cref="IHttpContextAccessor"/> → RequestServices), so
/// per-key <c>AllowedTools</c>/<c>AllowedSourceIds</c>/write gates apply to A2A
/// delegations exactly as they do to MCP <c>tools/call</c>.
///
/// Skill selection: <c>message.metadata["skill"]</c> (or <c>"tool"</c>) names
/// the skill/tool; absent → <c>ask_knowledge</c>. Extra arguments ride in
/// <c>message.metadata["arguments"]</c> (JSON object).
/// </summary>
public sealed class KnowledgeHubA2AAgent(IHttpContextAccessor http) : IAgentHandler
{
    /// <summary>Skills this agent advertises and accepts (SPEC RF-001).</summary>
    internal static readonly HashSet<string> DelegableSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_knowledge", "search_knowledge", "agent_chat", "read_document"
    };

    private const string DefaultSkill = "ask_knowledge";

    public async Task ExecuteAsync(
        RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var services = http.HttpContext?.RequestServices;
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger(nameof(KnowledgeHubA2AAgent));
        var (skill, arguments, routeError) = ResolveInvocation(context);

        using var span = KnowledgeHubActivity.Start("a2a.serve");
        span?.SetTag("a2a.skill", skill ?? "rejected");

        // Simple (non-task) message path: reply via MessageResponder.
        if (string.IsNullOrEmpty(context.TaskId))
        {
            var responder = new MessageResponder(eventQueue, context.ContextId);
            if (routeError is not null || services is null || skill is null)
            {
                KnowledgeHubMetrics.A2ARequests.Add(1, new TagList { { "outcome", "rejected" } });
                await responder.ReplyAsync(
                    routeError ?? "A2A handler unavailable", cancellationToken: cancellationToken);
                return;
            }

            var (ok, parts0, _) = await InvokeAsync(services, skill, arguments, context, span, cancellationToken);
            KnowledgeHubMetrics.A2ARequests.Add(1, new TagList { { "outcome", ok ? "ok" : "error" } });
            await responder.ReplyAsync(LastText(parts0), cancellationToken: cancellationToken);
            return;
        }

        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        if (!context.IsContinuation)
            await updater.SubmitAsync(cancellationToken);

        if (routeError is not null || services is null || skill is null)
        {
            KnowledgeHubMetrics.A2ARequests.Add(1, new TagList { { "outcome", "rejected" } });
            await updater.FailAsync(AgentMessage(routeError ?? "A2A handler unavailable", context.ContextId), cancellationToken);
            return;
        }

        await updater.StartWorkAsync(cancellationToken: cancellationToken);

        var (success, parts, resultJson) = await InvokeAsync(
            services, skill, arguments, context, span, cancellationToken);

        await updater.AddArtifactAsync(parts, artifactId: $"art_{skill}",
            name: skill, lastChunk: true, cancellationToken: cancellationToken);
        var done = AgentMessage(
            parts.FirstOrDefault(p => p.Text is not null)?.Text ?? (success ? "done" : "failed"),
            context.ContextId);
        if (success)
            await updater.CompleteAsync(done, cancellationToken);
        else
            await updater.FailAsync(done, cancellationToken);

        KnowledgeHubMetrics.A2ARequests.Add(1, new TagList { { "outcome", success ? "ok" : "error" } });

        // RF-005: evidence receipt for the delegated tool call (same chain as MCP).
        var apiKeyId = http.HttpContext?.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
        await EvidenceEmission.RecordToolAsync(
            services.GetService<IEvidenceChainService>(), logger,
            $"a2a:{context.ContextId}", apiKeyId, context.TaskId,
            skill, context.TaskId,
            arguments is null ? null : JsonSerializer.Serialize(arguments),
            resultJson, parent: null, cancellationToken);
    }

    public async Task CancelAsync(
        RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        => await new TaskUpdater(eventQueue, context.TaskId, context.ContextId)
            .CancelAsync(cancellationToken);

    private async Task<(bool Success, List<Part> Parts, string? ResultJson)> InvokeAsync(
        IServiceProvider services, string skill, IDictionary<string, JsonElement>? args,
        RequestContext context, Activity? span, CancellationToken ct)
    {
        var catalog = services.GetRequiredService<IDynamicToolCatalog>();
        var tool = (await catalog.GetToolsAsync(services, ct))
            .FirstOrDefault(t => t.Name == skill);
        if (tool is null)
        {
            var exists = (await catalog.GetUnfilteredToolsAsync(services, ct))
                .Any(t => t.Name == skill);
            var msg = exists
                ? $"skill '{skill}' is not available for this credential"
                : $"unknown skill '{skill}'";
            span?.SetTag("a2a.outcome", exists ? "denied" : "unknown");
            return (false, [Part.FromText(msg)], null);
        }

        var scope = services.GetService<ICallerScopeProvider>() is { } p
            ? await p.GetAsync(ct) : CallerScope.Unrestricted;
        if (!tool.ReadOnly && !scope.AllowWrite)
        {
            span?.SetTag("a2a.outcome", "denied");
            return (false, [Part.FromText($"skill '{skill}' requires write access")], null);
        }

        try
        {
            var result = await tool.Handler(
                new ToolCallContext { Services = services, Arguments = args }, ct);
            var parts = new List<Part>();
            var json = result.StructuredContent is { } sc
                && sc.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? sc.GetRawText()
                : JsonSerializer.Serialize(result.Content);
            foreach (var c in result.Content)
            {
                if (c is TextContentBlock tb && !string.IsNullOrEmpty(tb.Text))
                    parts.Add(Part.FromText(tb.Text));
            }
            if (parts.Count == 0)
                parts.Add(Part.FromText("(no output)"));
            parts.Add(new Part { Data = JsonDocument.Parse(json).RootElement });

            var ok = result.IsError is not true;
            span?.SetTag("a2a.outcome", ok ? "ok" : "tool_error");
            return (ok, parts, json);
        }
        catch (McpProtocolException ex)
        {
            span?.SetTag("a2a.outcome", "tool_error");
            return (false, [Part.FromText(ex.Message)], null);
        }
    }

    /// <summary>Resolves skill + tool arguments from the inbound message.</summary>
    private static (string? Skill, IDictionary<string, JsonElement>? Args, string? Error)
        ResolveInvocation(RequestContext context)
    {
        var skill = DefaultSkill;
        var args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var userText = context.UserText ?? string.Empty;

        if (context.Message?.Metadata is { } md)
        {
            if (md.TryGetValue("skill", out var s) || md.TryGetValue("tool", out s)
                || md.TryGetValue("skillId", out s))
            {
                if (s.ValueKind == JsonValueKind.String)
                    skill = s.GetString();
            }
            if (md.TryGetValue("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in a.EnumerateObject())
                    args[p.Name] = p.Value;
            }
        }

        if (skill is null || !DelegableSkills.Contains(skill))
            return (skill, null, $"skill '{skill}' is not delegable — allowed: {string.Join(", ", DelegableSkills)}");

        // Positional primary arg per skill when the caller didn't pass it.
        void Primary(string name)
        {
            if (!args.ContainsKey(name) && !string.IsNullOrWhiteSpace(userText))
                args[name] = JsonSerializer.SerializeToElement(userText);
        }

        switch (skill.ToLowerInvariant())
        {
            case "ask_knowledge": Primary("question"); break;
            case "search_knowledge": Primary("query"); break;
            case "read_document": Primary("path"); break;
            case "agent_chat":
                Primary("message");
                args.TryAdd("threadId",
                    JsonSerializer.SerializeToElement(context.ContextId ?? context.TaskId));
                break;
        }

        return (skill, args, null);
    }

    private static string LastText(List<Part> parts)
        => parts.LastOrDefault(p => p.Text is not null)?.Text ?? "(no output)";

    private static Message AgentMessage(string text, string? contextId) => new()
    {
        Role = A2ARole.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        ContextId = contextId,
        Parts = [Part.FromText(text)]
    };
}
