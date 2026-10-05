using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// Cache management and inspection service (SPEC-20260924-redis-cache-and-tool-caching).
/// </summary>
public interface ICacheManagerService
{
    Task<CacheStatsDto> GetStatsAsync(CancellationToken ct = default);
    Task<ClearCacheResultDto> ClearAllAsync(CancellationToken ct = default);
    void TrackKey(string key, long sizeBytes, TimeSpan? ttl = null, IEnumerable<string>? tags = null);
    void RemoveKey(string key);

    /// <summary>Drops every tracked entry carrying <paramref name="tag"/> —
    /// the registry counterpart of <c>HybridCache.RemoveByTagAsync</c>.</summary>
    void RemoveTag(string tag);

    /// <summary>SPEC-20260926-settings-ux-embeddings RF-003 +
    /// SPEC-20260926-cache-key-consistency RF-002: removes ONE cache entry —
    /// the real <c>IDistributedCache</c> value (L1+L2) and the tracked record
    /// only when the removal succeeds; publishes <c>cache-key:</c> on the
    /// invalidation bus so replicas drop their L1 copy too.</summary>
    Task<CacheKeyRemovalResult> RemoveEntryAsync(string key, CancellationToken ct = default);

    /// <summary>SPEC-20260926-cache-coherence-and-ttl RF-004: on a REMOTE
    /// <c>cache-clear</c>, drops every key THIS process tracked — otherwise the
    /// entries this replica wrote stay in the shared L2 and the next read
    /// repopulates stale data.</summary>
    Task ClearLocalTrackedAsync(CancellationToken ct = default);
}

/// <summary>Result of a per-key eviction — never lies about backend failures.</summary>
public sealed record CacheKeyRemovalResult
{
    /// <summary>Whether the key was in the tracked set.</summary>
    public required bool Tracked { get; init; }
    /// <summary>Whether the backend removal succeeded.</summary>
    public required bool Removed { get; init; }
    /// <summary>Bounded error digest when <see cref="Removed"/> is false.</summary>
    public string? Error { get; init; }
}
