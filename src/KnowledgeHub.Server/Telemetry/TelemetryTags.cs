namespace KnowledgeHub.Server.Telemetry;

/// <summary>
/// Allowlist of tag/attribute keys emitted by KnowledgeHub telemetry
/// (SPEC-20260923-observability-metrics — "no PII, query text, chunk content
/// or secrets in tags"). Unit tests assert emitted keys stay inside this set.
/// </summary>
public static class TelemetryTags
{
    /// <summary>Metric tag keys — the complete allowlist.</summary>
    public static readonly IReadOnlySet<string> AllowedMetricKeys = new HashSet<string>
    {
        "mode", "cache_hit", "provider", "model", "store", "kind", "tool",
        "status", "region", "method", "session_mode", "succeeded", "reason",
        // SPEC-20260928: resilience fallback + observability follow-ups.
        "capability", "from", "to", "trigger", "outcome", "event_type"
    };

    /// <summary>Activity attribute keys — the complete allowlist.</summary>
    public static readonly IReadOnlySet<string> AllowedActivityKeys = new HashSet<string>
    {
        "search.mode", "search.topK", "vector.store", "llm.model", "llm.kind",
        "tool.name", "sync.sourceId", "sync.status", "agent.iteration",
        "mcp.method", "cache.hit", "search.floor.removed",
        "search.diversity.removed", "search.grade", "search.retried",
        "search.expansion.mode", "search.expansion.variants",
        "search.expansion.cached", "search.expansion.chunks",
        "search.graph.hits",
        // SPEC-20260928-observability-followups: temporal/live-action/evidence spans.
        "mode", "nominations", "receipts", "kind"
    };

    /// <summary>Cache region names derived from key prefixes — bounded set.</summary>
    public static string RegionFor(string cacheKey) => cacheKey switch
    {
        var k when k.StartsWith("emb:", StringComparison.Ordinal) => "embedding",
        var k when k.StartsWith("search:", StringComparison.Ordinal) => "search",
        var k when k.StartsWith("ans:", StringComparison.Ordinal) => "answer",
        var k when k.StartsWith("rewrite:", StringComparison.Ordinal) => "rewrite",
        var k when k.StartsWith("index:", StringComparison.Ordinal) => "indexVersion",
        var k when k.StartsWith("secret:", StringComparison.Ordinal) => "secret",
        var k when k.StartsWith("mcp:tool:", StringComparison.Ordinal) => "tool",
        _ => "other"
    };
}
