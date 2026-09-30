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
        async (ctx, ct) =>
        {
            var engine = ctx.Services?.GetService<IFallbackPolicyEngine>();
            var registry = ctx.Services?.GetService<ToolCapabilityRegistry>();
            if (engine is null || registry is null || engine.Mode is FallbackMode.Disabled)
                return await tool.Handler(ctx, ct);

            var logger = ctx.Services?.GetService<ILoggerFactory>()
                ?.CreateLogger("KnowledgeHub.Resilience.ToolFallback");
            var available = catalog.Keys.ToList();
            var visited = new HashSet<string>(StringComparer.Ordinal) { tool.Name };
            var current = tool;
            var currentArgs = ctx.Arguments;
            var attempt = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                CallToolResult result;
                try
                {
                    result = await current.Handler(
                        ctx with { Arguments = currentArgs }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           || !ct.IsCancellationRequested)
                {
                    var decision = engine.Evaluate(ex, "tools", attempt, ct);
                    if (!decision.ShouldFallback
                        || !TryNext(registry, catalog, available, visited,
                            current.Name, ctx.Arguments, out var next, out var mappedArgs))
                        throw;

                    KnowledgeHubMetrics.ToolFallbacks.Add(1,
                        new KeyValuePair<string, object?>("capability",
                            registry.GetCapabilityForTool(current.Name) ?? "unknown"),
                        new KeyValuePair<string, object?>("from", current.Name),
                        new KeyValuePair<string, object?>("to", next.Name),
                        new KeyValuePair<string, object?>("trigger", "exception"));
                    logger?.LogWarning(
                        ex,
                        "tool fallback: {From} → {To} after {Reason} (attempt {Attempt})",
                        current.Name, next.Name, decision.Reason, attempt + 1);
                    visited.Add(next.Name);
                    current = next;
                    currentArgs = mappedArgs;
                    attempt++;
                    continue;
                }

                if (result.IsError != true)
                    return result;

                var text = ExtractText(result);
                if (ToolErrorClassifier.Classify(text) is not ToolErrorClassifier.ToolErrorClass.Transient)
                    return result; // permanent/unknown errors surface as-is

                var reason = ToolErrorClassifier.ReasonFor(text);
                var decision2 = engine.EvaluateReason(reason, "tools", attempt, ct);
                if (!decision2.ShouldFallback
                    || !TryNext(registry, catalog, available, visited,
                        current.Name, ctx.Arguments, out var next2, out var mappedArgs2))
                    return result;

                KnowledgeHubMetrics.ToolFallbacks.Add(1,
                    new KeyValuePair<string, object?>("capability",
                        registry.GetCapabilityForTool(current.Name) ?? "unknown"),
                    new KeyValuePair<string, object?>("from", current.Name),
                    new KeyValuePair<string, object?>("to", next2.Name),
                    new KeyValuePair<string, object?>("trigger", "isError"));
                logger?.LogWarning(
                    "tool fallback: {From} → {To} after {Reason} (attempt {Attempt})",
                    current.Name, next2.Name, reason, attempt + 1);
                visited.Add(next2.Name);
                current = next2;
                currentArgs = mappedArgs2;
                attempt++;
            }
        };

    /// <summary>Picks the next same-capability tool that is visible to this
    /// caller (present in the scope-filtered catalog), read-only, not already
    /// tried, and whose required args the caller's arguments can satisfy
    /// (SPEC-20260929 RF-003 — never hand another tool a foreign arg shape).</summary>
    private static bool TryNext(
        ToolCapabilityRegistry registry,
        IReadOnlyDictionary<string, CatalogTool> catalog,
        IReadOnlyCollection<string> available,
        HashSet<string> visited,
        string current,
        IDictionary<string, System.Text.Json.JsonElement>? originalArgs,
        out CatalogTool next,
        out IDictionary<string, System.Text.Json.JsonElement>? mappedArgs)
    {
        foreach (var name in registry.CandidateToolNames(current, available)
            .Where(n => !visited.Contains(n)))
        {
            if (!catalog.TryGetValue(name, out var candidate))
                continue;
            if (!candidate.ReadOnly)
                continue; // never substitute a write-capable tool
            if (!MapArgs(candidate, originalArgs, out mappedArgs))
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
