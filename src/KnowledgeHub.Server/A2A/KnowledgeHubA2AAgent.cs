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
        "ask_knowledge", "search_knowledge", "agent_chat", "read_document",
        "write_knowledge", "write_note"
    };

    private const string DefaultSkill = "ask_knowledge";
    private const string OutcomeTag = "outcome";
    private const string SpanOutcomeTag = "a2a.outcome";

    public async Task ExecuteAsync(
        RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var services = http.HttpContext?.RequestServices;
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger(nameof(KnowledgeHubA2AAgent));
        var (skill, arguments, routeError) = ResolveInvocation(context);

        using var span = KnowledgeHubActivity.Start("a2a.serve");
        span?.SetTag("a2a.skill", skill ?? "rejected");

        // RF-004: write tools invoked downstream stamp frontmatter origin —
        // channel "a2a" + caller key + advertised agent name (when provided).
        if (services?.GetService<WriteOriginContext>() is { } origin)
        {
            origin.Channel = "a2a";
            origin.KeyId = http.HttpContext?.User
                .FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
            origin.AgentName = context.Message?.Metadata is { } md
                && md.TryGetValue("agentName", out var an)
                && an.ValueKind == JsonValueKind.String
                    ? an.GetString() : null;
        }

        var route = new InvocationRoute(skill, arguments, routeError);
        if (string.IsNullOrEmpty(context.TaskId))
        {
            await HandleMessageAsync(context, eventQueue, services, route, span, cancellationToken);
            return;
        }

        await HandleTaskAsync(context, eventQueue, services, logger, route, span, cancellationToken);
    }

    /// <summary>Resolved invocation: the routed skill, its arguments and any
    /// routing error — shared by the message and task paths.</summary>
    private sealed record InvocationRoute(
        string? Skill, IDictionary<string, JsonElement>? Arguments, string? RouteError);

    /// <summary>Simple (non-task) message path — replies via <see cref="MessageResponder"/>.</summary>
    private static async Task HandleMessageAsync(
        RequestContext context, AgentEventQueue eventQueue, IServiceProvider? services,
        InvocationRoute route, Activity? span, CancellationToken ct)
    {
        var responder = new MessageResponder(eventQueue, context.ContextId);
        if (route.RouteError is not null || services is null || route.Skill is null)
        {
            RecordOutcome("rejected");
            await responder.ReplyAsync(
                route.RouteError ?? "A2A handler unavailable", cancellationToken: ct);
            return;
        }

        var (ok, parts, _) = await InvokeAsync(services, route.Skill, route.Arguments, span, ct);
        RecordOutcome(ok ? "ok" : "error");
        await responder.ReplyAsync(LastText(parts), cancellationToken: ct);
    }

    /// <summary>Task path — Submit → Work → artifact → Complete/Fail, plus the
    /// RF-005 evidence receipt for the delegated tool call.</summary>
    private async Task HandleTaskAsync(
        RequestContext context, AgentEventQueue eventQueue, IServiceProvider? services,
        ILogger? logger, InvocationRoute route, Activity? span, CancellationToken ct)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        if (!context.IsContinuation)
            await updater.SubmitAsync(ct);

        if (route.RouteError is not null || services is null || route.Skill is null)
        {
            RecordOutcome("rejected");
            await updater.FailAsync(AgentMessage(route.RouteError ?? "A2A handler unavailable", context.ContextId), ct);
            return;
        }

        await updater.StartWorkAsync(cancellationToken: ct);

        // RF-002: incremental progress — agent_chat reports per iteration via
        // OnProgress; a heartbeat covers any other skill running >2s so the
        // caller sees working updates instead of a silent task.
        ValueTask ProgressAsync(string message, CancellationToken pctx) =>
            updater.StartWorkAsync(AgentMessage(message, context.ContextId), pctx);
        var work = InvokeAsync(services, route.Skill, route.Arguments, span, ct, ProgressAsync);
        var heartbeat = HeartbeatAsync(work, route.Skill, context.ContextId, updater, logger, ct);
        var (success, parts, resultJson) = await work;
        await heartbeat;

        await CompleteTaskAsync(updater, route.Skill, context.ContextId, success, parts, ct);
        await RecordEvidenceAsync(services, route, context, resultJson, logger, ct);
    }

    private static async Task CompleteTaskAsync(
        TaskUpdater updater, string skill, string? contextId,
        bool success, List<Part> parts, CancellationToken ct)
    {
        await updater.AddArtifactAsync(parts, artifactId: $"art_{skill}",
            name: skill, lastChunk: true, cancellationToken: ct);
        var done = AgentMessage(
            parts.FirstOrDefault(p => p.Text is not null)?.Text ?? (success ? "done" : "failed"),
            contextId);
        if (success)
            await updater.CompleteAsync(done, ct);
        else
            await updater.FailAsync(done, ct);

        RecordOutcome(success ? "ok" : "error");
    }

    /// <summary>RF-005 evidence receipt for the delegated tool call.</summary>
    private async Task RecordEvidenceAsync(
        IServiceProvider services, InvocationRoute route, RequestContext context,
        string? resultJson, ILogger? logger, CancellationToken ct)
    {
        var apiKeyId = http.HttpContext?.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
        await EvidenceEmission.RecordToolAsync(
            new EvidenceEmission.EmissionContext(
                services.GetService<IEvidenceChainService>(), $"a2a:{context.ContextId}", apiKeyId, logger),
            threadId: context.TaskId,
            route.Skill!,
            route.Arguments is null ? null : JsonSerializer.Serialize(route.Arguments),
            resultJson, parent: null, ct);
    }

    private static void RecordOutcome(string outcome) =>
        KnowledgeHubMetrics.A2ARequests.Add(1, new TagList { { OutcomeTag, outcome } });

    public async Task CancelAsync(
        RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        => await new TaskUpdater(eventQueue, context.TaskId, context.ContextId)
            .CancelAsync(cancellationToken);

    /// <summary>SPEC-20261001-a2a-task-durability RF-002: heartbeat working
    /// updates while a skill call is in-flight — covers any skill slower than
    /// ~2s (agent_chat also reports per-iteration lines via OnProgress).
    /// Exits when the work completes; never throws.</summary>
    private static async Task HeartbeatAsync(
        Task work, string skill, string? contextId, TaskUpdater updater,
        ILogger? logger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            while (!work.IsCompleted)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (await Task.WhenAny(work, delay) == work)
                    return;
                await updater.StartWorkAsync(
                    AgentMessage($"working — {skill} ({sw.Elapsed.TotalSeconds:F0}s)", contextId), ct);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger?.LogTrace(ex, "a2a progress heartbeat canceled");
        }
        // codeql[cs/catch-of-all-exceptions] background heartbeat — must not
        // take the agent turn down; logged at Debug.
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "a2a progress heartbeat stopped");
        }
    }

    private static async Task<(bool Success, List<Part> Parts, string? ResultJson)> InvokeAsync(
        IServiceProvider services, string skill, IDictionary<string, JsonElement>? args,
        Activity? span, CancellationToken ct,
        Func<string, CancellationToken, ValueTask>? progress = null)
    {
        var catalog = services.GetRequiredService<IDynamicToolCatalog>();
        var (tool, denial) = await ResolveToolAsync(catalog, services, skill, span, ct);
        if (tool is null)
            return (false, [Part.FromText(denial!)], null);

        if (await WriteDeniedAsync(services, tool, ct))
        {
            span?.SetTag(SpanOutcomeTag, "denied");
            return (false, [Part.FromText($"skill '{skill}' requires write access")], null);
        }

        return await CallToolAsync(services, tool, args, span, ct, progress);
    }

    /// <summary>Resolves the catalog tool; when it does not resolve, returns the
    /// denial message distinguishing "unknown skill" from "denied by credential"
    /// and tags the span accordingly.</summary>
    private static async Task<(CatalogTool? Tool, string? Denial)> ResolveToolAsync(
        IDynamicToolCatalog catalog, IServiceProvider services, string skill,
        Activity? span, CancellationToken ct)
    {
        var tool = (await catalog.GetToolsAsync(services, ct))
            .FirstOrDefault(t => t.Name == skill);
        if (tool is not null)
            return (tool, null);

        var exists = (await catalog.GetUnfilteredToolsAsync(services, ct))
            .Any(t => t.Name == skill);
        span?.SetTag(SpanOutcomeTag, exists ? "denied" : "unknown");
        return (null, exists
            ? $"skill '{skill}' is not available for this credential"
            : $"unknown skill '{skill}'");
    }

    private static async Task<bool> WriteDeniedAsync(
        IServiceProvider services, CatalogTool tool, CancellationToken ct)
    {
        if (tool.ReadOnly)
            return false;
        var scope = services.GetService<ICallerScopeProvider>() is { } p
            ? await p.GetAsync(ct) : CallerScope.Unrestricted;
        return !scope.AllowWrite;
    }

    /// <summary>Invokes the catalog handler and maps content/structured content
    /// to A2A parts (text parts first, structured payload as a data part).</summary>
    private static async Task<(bool, List<Part>, string?)> CallToolAsync(
        IServiceProvider services, CatalogTool tool,
        IDictionary<string, JsonElement>? args, Activity? span, CancellationToken ct,
        Func<string, CancellationToken, ValueTask>? progress = null)
    {
        try
        {
            var result = await tool.Handler(
                new ToolCallContext { Services = services, Arguments = args, OnProgress = progress }, ct);
            var json = result.StructuredContent is { } sc
                && sc.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? sc.GetRawText()
                : JsonSerializer.Serialize(result.Content);
            var parts = result.Content
                .OfType<TextContentBlock>()
                .Where(tb => !string.IsNullOrEmpty(tb.Text))
                .Select(tb => Part.FromText(tb.Text))
                .ToList();
            if (parts.Count == 0)
                parts.Add(Part.FromText("(no output)"));
            parts.Add(new Part { Data = JsonDocument.Parse(json).RootElement });

            var ok = result.IsError is not true;
            span?.SetTag(SpanOutcomeTag, ok ? "ok" : "tool_error");
            return (ok, parts, json);
        }
        catch (McpProtocolException ex)
        {
            span?.SetTag(SpanOutcomeTag, "tool_error");
            return (false, [Part.FromText(ex.Message)], null);
        }
    }

    /// <summary>Resolves skill + tool arguments from the inbound message.</summary>
    private static (string? Skill, IDictionary<string, JsonElement>? Args, string? Error)
        ResolveInvocation(RequestContext context)
    {
        string? skill = DefaultSkill;
        var args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var userText = context.UserText ?? string.Empty;

        if (context.Message?.Metadata is { } md)
            ApplyMetadata(md, ref skill, args);

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
            case "write_knowledge":
            case "write_note":
                Primary("content");
                break;
            case "agent_chat":
                Primary("prompt");
                // No threadId auto-map: A2A contextIds share the Guid format
                // but are not ConversationThread rows — RunAsync throws on a
                // missing thread. Callers pass arguments.threadId explicitly.
                break;
        }

        return (skill, args, null);
    }

    /// <summary>Reads the optional A2A message metadata: <c>skill</c> (or
    /// <c>tool</c>/<c>skillId</c>) selects the catalog tool; <c>arguments</c>
    /// is merged verbatim into the invocation args.</summary>
    private static void ApplyMetadata(
        IReadOnlyDictionary<string, JsonElement> metadata,
        ref string? skill, Dictionary<string, JsonElement> args)
    {
        if ((metadata.TryGetValue("skill", out var s) || metadata.TryGetValue("tool", out s)
            || metadata.TryGetValue("skillId", out s))
            && s.ValueKind == JsonValueKind.String)
        {
            skill = s.GetString();
        }
        if (metadata.TryGetValue("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in a.EnumerateObject())
                args[p.Name] = p.Value;
        }
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
