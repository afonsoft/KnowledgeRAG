namespace KnowledgeHub.Server.Configuration;

/// <summary>
/// Distributed-cache configuration (SPEC-20260916-performance-memory-cache
/// RF-005). <c>memory</c> (default) keeps the zero-infra single-process
/// deploy; <c>redis</c> switches <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// to StackExchangeRedis for multi-replica/restart-surviving entries.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary><c>memory</c> (default) | <c>redis</c>.</summary>
    public string Provider { get; set; } = "memory";

    /// <summary>Whether tool execution caching is enabled (SPEC-20260924-redis-cache-and-tool-caching RF-002).</summary>
    public bool ToolCacheEnabled { get; set; } = true;

    /// <summary>TTL in minutes for cached tool calls (minimum 60 minutes / 1 hour per user specification).</summary>
    public int ToolCacheTtlMinutes { get; set; } = 60;

    /// <summary>SPEC-20260925-cache-region-ttl-policies RF-001/RF-002: TTL
    /// (minutes) por região de chave — override via
    /// <c>Cache:RegionTtlMinutes:emb=1440</c> etc. Unknown regions →
    /// <see cref="DefaultTtlMinutes"/>.</summary>
    public Dictionary<string, int> RegionTtlMinutes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["emb"] = 1440,        // 24h — embeddings are content-addressed, long-lived
        ["search"] = 5,
        ["ans"] = 10,
        ["mcp:tool"] = 60,
        ["rewrite"] = 1440,    // parity with previous hardcoded 24h
        ["expand"] = 60,       // parity with previous Search:QueryExpansion:Ttl default
        ["index"] = 10080,     // 7d — version token must outlive everything
        ["secret"] = 60,
        ["settings"] = 1,      // describe DTOs — explicit evict on every write; TTL is only the out-of-band bound
        ["list"] = 2,          // list payloads (sources/api-keys) — tag invalidation on every mutation
        ["a2a"] = 60,          // agent card — config changes only via redeploy
        ["mcp:proxy-tools"] = 5, // upstream tools/list protos — per-source fingerprint already re-keys
    };

    /// <summary>Fallback when a key's region isn't in <see cref="RegionTtlMinutes"/>.</summary>
    public int DefaultTtlMinutes { get; set; } = 10;

    /// <summary>SPEC-20260925-hybrid-cache-l1l2 RF-001: L1 (in-process) in front
    /// of the distributed L2 — only relevant when Provider=redis.</summary>
    public bool L1Enabled { get; set; } = true;

    /// <summary>L1 ceiling: entries never live longer than this locally —
    /// bounded staleness without pub/sub invalidation (RF-003).</summary>
    public int L1MaxTtlMinutes { get; set; } = 5;

    /// <summary>StackExchange.Redis connection string — required when
    /// Provider=redis. Use <c>defaultDatabase=N</c> to pin the logical DB.</summary>
    public RedisCacheOptions Redis { get; set; } = new();

    public sealed class RedisCacheOptions
    {
        public string ConnectionString { get; set; } = "";
    }
}
