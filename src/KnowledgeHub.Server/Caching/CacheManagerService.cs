using System.Collections.Concurrent;
using System.Diagnostics;
using KnowledgeHub.Server.Configuration;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// Default implementation of <see cref="ICacheManagerService"/> (SPEC-20260924-redis-cache-and-tool-caching).
/// Tracks active keys, sizes, expiration times and provides cache stats and clear operations.
/// </summary>
public sealed class CacheManagerService : ICacheManagerService
{
    private sealed record CacheEntryMeta(string Key, string Prefix, long SizeBytes, DateTimeOffset? ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntryMeta> _trackedKeys = new(StringComparer.Ordinal);
    private readonly IDistributedCache _cache;
    private readonly IMemoryCache? _memoryCache;
    private readonly CacheOptions _options;
    private readonly ILogger<CacheManagerService> _logger;
    private readonly StackExchange.Redis.IConnectionMultiplexer? _redis;
    private readonly ICacheInvalidationBus? _bus;
    private long _hits;
    private long _misses;

    public static ICacheManagerService? Current { get; private set; }

    public CacheManagerService(
        IDistributedCache cache,
        IOptions<CacheOptions> options,
        ILogger<CacheManagerService> logger,
        IMemoryCache? memoryCache = null,
        StackExchange.Redis.IConnectionMultiplexer? redis = null,
        ICacheInvalidationBus? bus = null)
    {
        _cache = cache;
        _memoryCache = memoryCache;
        _options = options.Value;
        _logger = logger;
        _redis = redis;
        _bus = bus;
        Current = this;
    }

    public void TrackKey(string key, long sizeBytes, TimeSpan? ttl = null)
    {
        var prefix = ExtractPrefix(key);
        var expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : (DateTimeOffset?)null;
        _trackedKeys[key] = new CacheEntryMeta(key, prefix, sizeBytes, expiresAt);
    }

    public void RemoveKey(string key)
    {
        _trackedKeys.TryRemove(key, out _);
    }

    public void RecordHit(bool hit)
    {
        if (hit) Interlocked.Increment(ref _hits);
        else Interlocked.Increment(ref _misses);
    }

    /// <summary>SPEC-20260925-redis-health-and-scan-stats RF-003: SCAN caps —
    /// bounded page size and total keys so stats never stall the endpoint.</summary>
    private const int ScanMaxKeys = 500;
    private const int ScanPageSize = 200;
    private static readonly TimeSpan ScanDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RedisPingTimeout = TimeSpan.FromSeconds(2);

    public async Task<CacheStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        PruneExpired();

        var provider = _options.Provider.ToLowerInvariant();
        var keysList = _trackedKeys.Values
            .OrderByDescending(k => k.SizeBytes)
            .Take(1000)
            .Select(k => new CacheKeyItemDto
            {
                Key = k.Key,
                Prefix = k.Prefix,
                SizeBytes = k.SizeBytes,
                ExpiresInSeconds = k.ExpiresAt.HasValue
                    ? Math.Max(0, (long)(k.ExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds)
                    : null
            })
            .ToList();

        var totalSize = _trackedKeys.Values.Sum(k => k.SizeBytes);
        var stats = new CacheStatsDto
        {
            Provider = provider,
            IsConnected = true,
            TotalKeys = _trackedKeys.Count,
            TotalSizeBytes = totalSize,
            Hits = _hits,
            Misses = _misses,
            Keys = keysList
        };

        // RF-002: with redis, overlay server-side stats — tracked keys reflect
        // only this process; SCAN/INFO report the shared cache truth.
        if (provider == "redis" && _redis is not null)
            await EnrichFromRedisAsync(stats, ct);

        return stats;
    }

    private async Task EnrichFromRedisAsync(CacheStatsDto stats, CancellationToken ct)
    {
        // SPEC-20260926-redis-stats-admin-and-connflag RF-002: connectivity is
        // judged by PING alone — SCAN/INFO failures degrade the stats section
        // (StatsError), never flip the badge to disconnected.
        try
        {
            var ping = _redis!.GetDatabase().PingAsync();
            var completed = await Task.WhenAny(ping, Task.Delay(RedisPingTimeout, ct));
            stats.IsConnected = completed == ping && !ping.IsFaulted;
            if (ping.IsFaulted)
                _logger.LogWarning(ping.Exception?.GetBaseException(), "redis ping failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            stats.IsConnected = false;
            _logger.LogWarning(ex, "redis ping failed");
            return;
        }

        if (!stats.IsConnected)
            return;

        try
        {
            var server = _redis.GetEndPoints()
                .Select(ep => _redis.GetServer(ep))
                .FirstOrDefault(s => s.IsConnected);
            if (server is null)
            {
                stats.StatsError = "no connected server endpoint";
                return;
            }

            // Bounded SCAN (never KEYS *) — cap both page size and total count.
            // RF-006 (SPEC-20260926-cache-coherence-and-ttl): honor the caller's
            // cancellation — a stalled SCAN must not pin the settings endpoint.
            var count = 0L;
            var truncated = false;
            // RF-304 (SPEC-20260926-review-backlog-remediation): ct is only
            // checked per delivered key — a stalled page ignores cancellation.
            // A wall-clock deadline bounds the wait independent of page yield.
            var deadline = Stopwatch.StartNew();
            foreach (var _ in server.Keys(pattern: "*", pageSize: ScanPageSize))
            {
                ct.ThrowIfCancellationRequested();
                if (++count >= ScanMaxKeys || deadline.Elapsed >= ScanDeadline) { truncated = true; break; }
            }
            stats.ServerKeys = count;
            stats.Partial = truncated;

            var info = await server.InfoAsync("memory");
            var clients = await server.InfoAsync("clients");
            stats.ServerUsedMemoryBytes = ParseInfoLong(info, "used_memory");
            stats.ServerConnectedClients = (int?)ParseInfoLong(clients, "connected_clients");
            // SPEC-20260926-cache-key-consistency RF-003: ServerReported means
            // "server-side stats available" — only true once INFO answered;
            // a failed INFO leaves it false with StatsError describing why.
            stats.ServerReported = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            stats.StatsError = TrimError(ex);
            _logger.LogWarning(ex, "redis stats enrichment failed — connectivity ok, stats degraded");
        }
    }

    /// <inheritdoc />
    public async Task<CacheKeyRemovalResult> RemoveEntryAsync(string key, CancellationToken ct = default)
    {
        // SPEC-20260926-cache-key-consistency RF-002: untrack ONLY after the
        // backend removal succeeds — a failed delete must stay visible in the
        // panel and surface as an error, not a silent success.
        var tracked = _trackedKeys.ContainsKey(key);
        try
        {
            await _cache.RemoveAsync(key, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove key {Key} from cache", SafeCache.LogSafe(key));
            return new CacheKeyRemovalResult { Tracked = tracked, Removed = false, Error = TrimError(ex) };
        }

        _trackedKeys.TryRemove(key, out _);

        // SPEC-20260926-cache-key-consistency RF-001: propagate the eviction so
        // other replicas drop their L1 copy — L2 (redis) is already gone.
        if (_bus is not null)
        {
            try
            {
                await _bus.PublishAsync($"cache-key:{key}", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to publish cache-key invalidation");
            }
        }

        return new CacheKeyRemovalResult { Tracked = tracked, Removed = true };
    }

    private static string TrimError(Exception ex)
    {
        var b = ex.GetBaseException();
        var m = $"{b.GetType().Name}: {b.Message}".Replace('\n', ' ').Replace('\r', ' ');
        return m.Length > 200 ? m[..200] : m;
    }

    private static long? ParseInfoLong(
        IGrouping<string, KeyValuePair<string, string>>[] info,
        string key)
    {
        foreach (var section in info)
            foreach (var kv in section)
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(kv.Value, out var v))
                    return v;
        return null;
    }

    public async Task<ClearCacheResultDto> ClearAllAsync(CancellationToken ct = default)
    {
        var count = _trackedKeys.Count;
        foreach (var key in _trackedKeys.Keys)
        {
            try
            {
                await _cache.RemoveAsync(key, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove key {Key} from cache", SafeCache.LogSafe(key));
            }
        }

        _trackedKeys.Clear();

        // Invalidate index version to cascade-invalidate all versioned cache entries
        try
        {
            await SafeCache.SetStringAsync(_cache, CacheKeys.IndexVersion,
                Guid.NewGuid().ToString("N"), null, _logger, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to bump index version during cache clear");
        }

        if (_memoryCache is MemoryCache mc)
        {
            mc.Compact(1.0); // clears all compactable entries
        }

        // SPEC-20260925-distributed-invalidation-pubsub RF-002/RF-003: tell the
        // other replicas to drop their L1 tiers too.
        if (_bus is not null)
            await _bus.PublishAsync("cache-clear", ct);

        _logger.LogInformation("Cache completely cleared: {Count} keys purged", count);
        return new ClearCacheResultDto
        {
            Success = true,
            ClearedKeys = count,
            Message = $"Cache completamente limpo ({count} chaves purgadas com sucesso)."
        };
    }

    /// <inheritdoc />
    public async Task ClearLocalTrackedAsync(CancellationToken ct = default)
    {
        // RF-303 (SPEC-20260926-review-backlog-remediation): snapshot the keyset
        // at event time — keys written DURING this clear must survive (they
        // post-date the clear); a blanket Clear() also dropped their tracking.
        foreach (var key in _trackedKeys.Keys.ToArray())
        {
            try { await _cache.RemoveAsync(key, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "remote-clear: failed to remove {Key} from cache", SafeCache.LogSafe(key));
            }
            _trackedKeys.TryRemove(key, out _);
        }
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _trackedKeys.Where(kv => kv.Value.ExpiresAt <= now))
            _trackedKeys.TryRemove(kvp.Key, out _);
    }

    private static string ExtractPrefix(string key)
    {
        var idx = key.IndexOf(':');
        if (idx <= 0) return "general";
        var second = key.IndexOf(':', idx + 1);
        return second > 0 ? key[..second] : key[..idx];
    }
}
