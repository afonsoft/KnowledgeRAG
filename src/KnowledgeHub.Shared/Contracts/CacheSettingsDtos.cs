namespace KnowledgeHub.Shared.Contracts;

/// <summary>
/// Cache management and inspection DTOs (SPEC-20260924-redis-cache-and-tool-caching).
/// </summary>
public sealed class CacheStatsDto
{
    public string Provider { get; set; } = "memory";
    public bool IsConnected { get; set; } = true;
    public int TotalKeys { get; set; }
    public long TotalSizeBytes { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public IReadOnlyList<CacheKeyItemDto> Keys { get; set; } = [];

    /// <summary>SPEC-20260925-redis-health-and-scan-stats RF-002: true when
    /// <see cref="ServerKeys"/>/memory stats came from the Redis server itself
    /// (vs. this process's tracked keys).</summary>
    public bool ServerReported { get; set; }
    /// <summary>SCAN was capped — more keys exist than reported.</summary>
    public bool Partial { get; set; }
    public long? ServerKeys { get; set; }
    public long? ServerUsedMemoryBytes { get; set; }
    public int? ServerConnectedClients { get; set; }

    /// <summary>SPEC-20260926-redis-stats-admin-and-connflag RF-002: reason the
    /// server-side enrichment (SCAN/INFO) failed — connectivity verdict stays in
    /// <see cref="IsConnected"/> (PING only); this surfaces the stats error.</summary>
    public string? StatsError { get; set; }
}

public sealed class CacheKeyItemDto
{
    public string Key { get; set; } = "";
    public string Prefix { get; set; } = "";
    public long SizeBytes { get; set; }
    public long? ExpiresInSeconds { get; set; }

    /// <summary><c>"tracked"</c> — written and recorded by this process;
    /// <c>"server"</c> — discovered on the Redis server via SCAN (written by
    /// another replica; no size/TTL metadata available).</summary>
    public string Source { get; set; } = "tracked";

    /// <summary>HybridCache tags recorded at write time (tag-scoped
    /// invalidation), when the writer supplied any.</summary>
    public IReadOnlyList<string>? Tags { get; set; }
}

public sealed class ClearCacheResultDto
{
    public bool Success { get; set; }
    public int ClearedKeys { get; set; }
    public string Message { get; set; } = "";
}
