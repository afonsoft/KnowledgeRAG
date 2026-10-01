using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// SPEC-20260925-hybrid-cache-l1l2 RF-001/RF-002: two-tier
/// <see cref="IDistributedCache"/> — in-process L1 (<see cref="IMemoryCache"/>)
/// in front of the distributed L2 (Redis). Reads short-circuit on L1 hits
/// (no network RTT); L1 TTL is capped by <c>Cache:L1MaxTtlMinutes</c> so
/// staleness stays bounded even without pub/sub invalidation.
/// </summary>
public sealed class L1L2Cache : IDistributedCache
{
    private readonly IMemoryCache _l1;
    private readonly IDistributedCache _l2;
    private readonly TimeSpan _l1MaxTtl;
    private readonly Microsoft.Extensions.Logging.ILogger<L1L2Cache>? _logger;

    /// <summary>SPEC-20260925-hybrid-cache-l1l2 RF-002: locks serialize
    /// concurrent miss-fills so a hot key never spawns N producers.
    /// SPEC-20260926-cache-coherence-and-ttl RF-002: FIXED striped table — a
    /// per-key ConcurrentDictionary would grow unbounded with key cardinality.</summary>
    private const int LockStripes = 256;
    private readonly SemaphoreSlim[] _keyLocks =
        Enumerable.Range(0, LockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public L1L2Cache(IMemoryCache l1, IDistributedCache l2, TimeSpan l1MaxTtl,
        Microsoft.Extensions.Logging.ILogger<L1L2Cache>? logger = null)
    {
        _l1 = l1;
        _l2 = l2;
        _l1MaxTtl = l1MaxTtl;
        _logger = logger;
    }

    /// <summary>The distributed tier — pub/sub invalidation and server stats
    /// talk to this, not to L1.</summary>
    public IDistributedCache Inner => _l2;

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        if (_l1.TryGetValue(key, out byte[]? hit))
            return hit;

        var value = await _l2.GetAsync(key, token);
        if (value is not null)
            _l1.Set(key, value, _l1MaxTtl);
        return value;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).GetAwaiter().GetResult();

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        try
        {
            await _l2.SetAsync(key, value, options, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RF-006 (SPEC-20260926-cache-coherence-and-ttl): L2 down degrades to
            // local-only caching — better than losing the cache entirely.
            _logger?.LogWarning(ex, "L2 write failed — entry cached in L1 only");
        }
        var l1Ttl = _l1MaxTtl;
        if (options.AbsoluteExpirationRelativeToNow is { } ttl && ttl < l1Ttl)
            l1Ttl = ttl;
        _l1.Set(key, value, l1Ttl);
    }

    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        _l2.RefreshAsync(key, token);

    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        _l1.Remove(key);
        await _l2.RemoveAsync(key, token);
    }

    /// <summary>RF-002: wait on the striped fill lock; used by
    /// <see cref="SafeCache.GetOrCreateAsync"/> to collapse concurrent misses.</summary>
    internal SemaphoreSlim LockFor(string key) =>
        _keyLocks[(int)((uint)key.GetHashCode() % LockStripes)];

    /// <summary>SPEC-20260925-distributed-invalidation-pubsub RF-003: drop one
    /// L1 entry (the L2 is the source of truth — next read re-fetches).</summary>
    public void InvalidateLocal(string key) => _l1.Remove(key);

    /// <summary>Drop the entire local tier — e.g. a remote cache-clear.</summary>
    public void InvalidateAllLocal()
    {
        if (_l1 is MemoryCache mc)
            mc.Compact(1.0);
    }
}
