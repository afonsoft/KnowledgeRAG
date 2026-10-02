using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Resilience;

/// <summary>
/// Wraps read-only <see cref="CatalogTool"/> handlers with same-capability
/// fallback (SPEC-20260928-resilience-tool-fallback-wiring RF-001/RF-002):
/// on a transient failure — thrown exception or <c>IsError</c> result — the
/// <see cref="IFallbackPolicyEngine"/> evaluates the call and the next
/// provider in the capability chain answers with the same arguments.
/// Candidates come from the caller's scope-filtered catalog, so fallback can
/// never cross <see cref="Auth.CallerScope"/>. Write tools are never wrapped.
/// </summary>
public static class ResilientToolInvoker
{
    /// <summary>Returns tools with resilient handlers. Cheap per catalog
    /// rebuild — the policy engine resolves the effective mode lazily, so the
    /// fast path stays a pass-through when resilience is disabled.</summary>
    public static IReadOnlyList<CatalogTool> Wrap(IReadOnlyList<CatalogTool> tools)
    {
        var byName = new Dictionary<string, CatalogTool>(StringComparer.Ordinal);
        foreach (var t in tools)
            byName[t.Name] = t;

        return tools.Select(t => t.ReadOnly
                ? t with { Handler = WrapHandler(t, byName) }
                : t)
            .ToList();
    }

    private static Func<ToolCallContext, CancellationToken, ValueTask<CallToolResult>> WrapHandler(
        CatalogTool tool, IReadOnlyDictionary<string, CatalogTool> catalog) =>
        (ctx, ct) => InvokeWithFallbackAsync(tool, catalog, ctx, ct);

    private static async ValueTask<CallToolResult> InvokeWithFallbackAsync(
        CatalogTool tool, IReadOnlyDictionary<string, CatalogTool> catalog,
        ToolCallContext ctx, CancellationToken ct)
    {
        var engine = ctx.Services?.GetService<IFallbackPolicyEngine>();
        var registry = ctx.Services?.GetService<ToolCapabilityRegistry>();
        if (engine is null || registry is null || engine.Mode is FallbackMode.Disabled)
            return await tool.Handler(ctx, ct);

        var logger = ctx.Services?.GetService<ILoggerFactory>()
            ?.CreateLogger("KnowledgeHub.Resilience.ToolFallback");
        var env = new FallbackEnv(registry, catalog, catalog.Keys.ToList(),
            new HashSet<string>(StringComparer.Ordinal) { tool.Name },
            ctx.Arguments, logger);
        var current = tool;
        var currentArgs = ctx.Arguments;
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var step = await AttemptAsync(env, engine, current, ctx, currentArgs, attempt, ct);
            if (step.Result is { } result)
                return result;
            current = step.NextTool!;
            currentArgs = step.NextArgs;
            attempt++;
        }
    }

    /// <summary>One invocation attempt: run the current handler; on a transient
    /// failure (thrown or IsError) pick the next fallback candidate. A null
    /// <see cref="FallbackStep.Result"/> means "advance to NextTool".</summary>
    private static async ValueTask<FallbackStep> AttemptAsync(
        FallbackEnv env, IFallbackPolicyEngine engine, CatalogTool current,
        ToolCallContext ctx, IDictionary<string, System.Text.Json.JsonElement>? args,
        int attempt, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await current.Handler(ctx with { Arguments = args }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !ct.IsCancellationRequested)
        {
            var decision = engine.Evaluate(ex, "tools", attempt, ct);
            var (nextTool, nextArgs) = AdvanceFallback(env, current.Name,
                "exception", decision.Reason, decision.ShouldFallback, ex);
            if (nextTool is null)
                throw;
            return new FallbackStep(null, nextTool, nextArgs);
        }

        if (result.IsError != true)
            return new FallbackStep(result, null, null);

        var text = ExtractText(result);
        if (ToolErrorClassifier.Classify(text) is not ToolErrorClassifier.ToolErrorClass.Transient)
            return new FallbackStep(result, null, null); // permanent/unknown errors surface as-is

        var reason = ToolErrorClassifier.ReasonFor(text);
        var decision2 = engine.EvaluateReason(reason, "tools", attempt, ct);
        var (nextTool2, nextArgs2) = AdvanceFallback(env, current.Name,
            "isError", reason, decision2.ShouldFallback, null);
        return nextTool2 is null
            ? new FallbackStep(result, null, null)
            : new FallbackStep(null, nextTool2, nextArgs2);
    }

    /// <summary>Outcome of one <see cref="AttemptAsync"/>: either a final result
    /// to surface, or the next candidate and its mapped arguments.</summary>
    private sealed record FallbackStep(
        CallToolResult? Result,
        CatalogTool? NextTool,
        IDictionary<string, System.Text.Json.JsonElement>? NextArgs);

    /// <summary>Fallback environment shared across attempts.</summary>
    private sealed record FallbackEnv(
        ToolCapabilityRegistry Registry,
        IReadOnlyDictionary<string, CatalogTool> Catalog,
        IReadOnlyCollection<string> Available,
        HashSet<string> Visited,
        IDictionary<string, System.Text.Json.JsonElement>? OriginalArgs,
        ILogger? Logger);

    /// <summary>Evaluates the fallback decision, picks the next candidate and
    /// records the transition. Returns null when no fallback should happen.</summary>
    private static (CatalogTool? Next, IDictionary<string, System.Text.Json.JsonElement>? Args)
        AdvanceFallback(
            FallbackEnv env, string currentName, string trigger, string reason,
            bool shouldFallback, Exception? ex)
    {
        if (!shouldFallback
            || !TryNext(new FallbackProbe(env.Registry, env.Catalog, env.Available,
                env.Visited, currentName, env.OriginalArgs), out var next, out var mappedArgs))
            return (null, null);

        KnowledgeHubMetrics.ToolFallbacks.Add(1,
            new KeyValuePair<string, object?>("capability",
                env.Registry.GetCapabilityForTool(currentName) ?? "unknown"),
            new KeyValuePair<string, object?>("from", currentName),
            new KeyValuePair<string, object?>("to", next.Name),
            new KeyValuePair<string, object?>("trigger", trigger));
        if (ex is not null)
            env.Logger?.LogWarning(ex,
                "tool fallback: {From} → {To} after {Reason} (attempt {Attempt})",
                currentName, next.Name, reason, env.Visited.Count);
        else
            env.Logger?.LogWarning(
                "tool fallback: {From} → {To} after {Reason} (attempt {Attempt})",
                currentName, next.Name, reason, env.Visited.Count);
        env.Visited.Add(next.Name);
        return (next, mappedArgs);
    }

    /// <summary>Fallback search state for <see cref="TryNext"/> — the caller's
    /// environment (registry/catalog/available) plus the probe cursor.</summary>
    private sealed record FallbackProbe(
        ToolCapabilityRegistry Registry,
        IReadOnlyDictionary<string, CatalogTool> Catalog,
        IReadOnlyCollection<string> Available,
        HashSet<string> Visited,
        string Current,
        IDictionary<string, System.Text.Json.JsonElement>? OriginalArgs);

    /// <summary>Picks the next same-capability tool that is visible to this
    /// caller (present in the scope-filtered catalog), read-only, not already
    /// tried, and whose required args the caller's arguments can satisfy
    /// (SPEC-20260929 RF-003 — never hand another tool a foreign arg shape).</summary>
    private static bool TryNext(
        FallbackProbe probe,
        out CatalogTool next,
        out IDictionary<string, System.Text.Json.JsonElement>? mappedArgs)
    {
        foreach (var candidate in probe.Registry.CandidateToolNames(probe.Current, probe.Available)
            .Where(n => !probe.Visited.Contains(n)
                && probe.Catalog.TryGetValue(n, out var c) && c.ReadOnly)
            .Select(n => probe.Catalog[n]))
        {
            if (!MapArgs(candidate, probe.OriginalArgs, out mappedArgs))
                continue; // schema-incompatible — args would be meaningless
            next = candidate;
            return true;
        }
        next = default!;
        mappedArgs = null;
        return false;
    }

    /// <summary>RF-003: the substitute is viable only when every required
    /// schema property is present in the original args with a compatible JSON
    /// type; undeclared extras are dropped from the mapped set.</summary>
    internal static bool MapArgs(
        CatalogTool candidate,
        IDictionary<string, System.Text.Json.JsonElement>? args,
        out IDictionary<string, System.Text.Json.JsonElement>? mapped)
    {
        mapped = null;
        var schema = candidate.InputSchema;
        var required = schema["required"]?.AsArray()
            .Select(n => n?.GetValue<string>())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var props = schema["properties"] as System.Text.Json.Nodes.JsonObject;

        if (args is null || args.Count == 0)
        {
            if (required.Count > 0)
                return false; // substitute needs args the caller never sent
            mapped = null;
            return true;
        }

        var mappedDict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            if (props is null || props[key] is not System.Text.Json.Nodes.JsonObject prop)
            {
                if (required.Contains(key))
                    return false; // required arg but schema declares no shape for it
                continue; // undeclared extra — dropped, not forwarded
            }
            var declaredType = prop["type"]?.GetValue<string>();
            var compatible = declaredType switch
            {
                null => true,
                "string" => value.ValueKind is System.Text.Json.JsonValueKind.String,
                "integer" or "number" => value.ValueKind is System.Text.Json.JsonValueKind.Number,
                "boolean" => value.ValueKind is System.Text.Json.JsonValueKind.True
                             or System.Text.Json.JsonValueKind.False,
                "array" => value.ValueKind is System.Text.Json.JsonValueKind.Array,
                "object" => value.ValueKind is System.Text.Json.JsonValueKind.Object,
                _ => true // unknown/nullable type unions — let the tool decide
            };
            if (!compatible)
            {
                if (required.Contains(key))
                    return false;
                continue;
            }
            mappedDict[key] = value;
        }

        if (required.Any(r => !mappedDict.ContainsKey(r)))
            return false;
        mapped = mappedDict;
        return true;
    }

    private static string? ExtractText(CallToolResult result) =>
        result.Content is null
            ? null
            : string.Join(' ', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
