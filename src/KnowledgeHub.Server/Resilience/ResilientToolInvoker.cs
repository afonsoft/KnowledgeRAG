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
            var attempt = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                CallToolResult result;
                try
                {
                    result = await current.Handler(ctx, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           || !ct.IsCancellationRequested)
                {
                    var decision = engine.Evaluate(ex, "tools", attempt, ct);
                    if (!decision.ShouldFallback
                        || !TryNext(registry, catalog, available, visited, current.Name, out var next))
                        throw;

                    KnowledgeHubMetrics.ToolFallbacks.Add(1,
                        new KeyValuePair<string, object?>("capability",
                            registry.GetCapabilityForTool(current.Name) ?? "unknown"),
                        new KeyValuePair<string, object?>("from", current.Name),
                        new KeyValuePair<string, object?>("to", next.Name),
                        new KeyValuePair<string, object?>("trigger", "exception"));
                    logger?.LogWarning(
                        "tool fallback: {From} → {To} after {Reason} (attempt {Attempt})",
                        current.Name, next.Name, decision.Reason, attempt + 1);
                    visited.Add(next.Name);
                    current = next;
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
                    || !TryNext(registry, catalog, available, visited, current.Name, out var next2))
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
                attempt++;
            }
        };

    /// <summary>Picks the next same-capability tool that is visible to this
    /// caller (present in the scope-filtered catalog), read-only, and not
    /// already tried in this chain.</summary>
    private static bool TryNext(
        ToolCapabilityRegistry registry,
        IReadOnlyDictionary<string, CatalogTool> catalog,
        IReadOnlyCollection<string> available,
        HashSet<string> visited,
        string current,
        out CatalogTool next)
    {
        foreach (var name in registry.CandidateToolNames(current, available)
            .Where(n => !visited.Contains(n)))
        {
            if (!catalog.TryGetValue(name, out var candidate))
                continue;
            if (!candidate.ReadOnly)
                continue; // never substitute a write-capable tool
            next = candidate;
            return true;
        }
        next = default!;
        return false;
    }

    private static string? ExtractText(CallToolResult result) =>
        result.Content is null
            ? null
            : string.Join(' ', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
