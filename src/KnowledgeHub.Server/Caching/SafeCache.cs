using System.Text.Json;
using KnowledgeHub.Server.Telemetry;
using Microsoft.Extensions.Caching.Distributed;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// Fail-soft <see cref="IDistributedCache"/> helpers
/// (SPEC-20260916-performance-memory-cache RNF-003): a down/unreachable cache
/// backend logs a warning and behaves as a miss — it never fails a request.
/// </summary>
public static class SafeCache
{
    public static async Task<string?> GetStringAsync(
        IDistributedCache cache, string key, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var value = await cache.GetStringAsync(key, ct);
            RecordHit(key, value is not null);
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "cache get failed for {Key} — treating as miss", key);
            RecordHit(key, false);
            return null;
        }
    }

    public static async Task<byte[]?> GetAsync(
        IDistributedCache cache, string key, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var value = await cache.GetAsync(key, ct);
            RecordHit(key, value is not null);
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "cache get failed for {Key} — treating as miss", key);
            RecordHit(key, false);
            return null;
        }
    }

    /// <summary>Records hit/miss tagged by bounded cache region (RF-004).</summary>
    private static void RecordHit(string key, bool hit)
    {
        var tag = new KeyValuePair<string, object?>("region", TelemetryTags.RegionFor(key));
        if (hit)
            KnowledgeHubMetrics.CacheHits.Add(1, tag);
        else
            KnowledgeHubMetrics.CacheMisses.Add(1, tag);

        (CacheManagerService.Current as CacheManagerService)?.RecordHit(hit);
    }

    public static async Task SetStringAsync(
        IDistributedCache cache, string key, string value, TimeSpan? ttl,
        ILogger logger, CancellationToken ct = default)
    {
        try
        {
            ttl ??= ResolveTtl(key);
            await cache.SetStringAsync(key, value,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
            CacheManagerService.Current?.TrackKey(key, System.Text.Encoding.UTF8.GetByteCount(value), ttl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "cache set failed for {Key} — skipping", key);
        }
    }

    public static async Task SetAsync(
        IDistributedCache cache, string key, byte[] value, TimeSpan? ttl,
        ILogger logger, CancellationToken ct = default)
    {
        try
        {
            ttl ??= ResolveTtl(key);
            await cache.SetAsync(key, value,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
            CacheManagerService.Current?.TrackKey(key, value.Length, ttl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "cache set failed for {Key} — skipping", key);
        }
    }

    public static async Task RemoveAsync(
        IDistributedCache cache, string key,
        ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await cache.RemoveAsync(key, ct);
            CacheManagerService.Current?.RemoveKey(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "cache remove failed for {Key} — skipping", key);
        }
    }

    /// <summary>SPEC-20260925-cache-region-ttl-policies RF-001: TTL omitted →
    /// region policy; override falls back to a sane default.</summary>
    private static TimeSpan ResolveTtl(string key) =>
        CacheTtlPolicy.Current?.For(key) ?? TimeSpan.FromMinutes(10);

    /// <summary>Serialize + set a JSON payload.</summary>
    public static Task SetJsonAsync<T>(
        IDistributedCache cache, string key, T value, TimeSpan? ttl,
        ILogger logger, CancellationToken ct = default) =>
        SetStringAsync(cache, key, JsonSerializer.Serialize(value), ttl, logger, ct);

    /// <summary>SPEC-20260925-hybrid-cache-l1l2 RF-002: get-or-create with
    /// stampede protection — concurrent misses on a hot key share ONE producer
    /// (per-key lock); other waiters re-read the cache after the lock.</summary>
    /// <summary>Serializer pair for <see cref="GetOrCreateAsync{T}"/> payloads.</summary>
    public sealed record CacheCodec<T>(Func<T, byte[]> Serialize, Func<byte[], T?> Deserialize);

    public static async Task<T?> GetOrCreateAsync<T>(
        IDistributedCache cache, string key,
        Func<CancellationToken, Task<T?>> factory,
        CacheCodec<T> codec,
        TimeSpan? ttl, ILogger logger, CancellationToken ct = default)
    {
        var cached = await GetAsync(cache, key, logger, ct);
        if (cached is not null)
            return codec.Deserialize(cached);

        if (cache is L1L2Cache hybrid)
        {
            var gate = hybrid.LockFor(key);
            await gate.WaitAsync(ct);
            try
            {
                // Second cache check under the lock — the first waiter filled it;
                // only the winner produces, serializing the miss-fill.
                cached = await GetAsync(cache, key, logger, ct);
                if (cached is not null)
                    return codec.Deserialize(cached);
                var produced = await factory(ct);
                if (produced is not null)
                    await SetAsync(cache, key, codec.Serialize(produced), ttl, logger, ct);
                return produced;
            }
            finally { gate.Release(); }
        }

        var produced2 = await factory(ct);
        if (produced2 is not null)
            await SetAsync(cache, key, codec.Serialize(produced2), ttl, logger, ct);
        return produced2;
    }

    /// <summary>SPEC-20260926-cache-key-consistency RF-004: strip CR/LF from
    /// user-controlled values before they reach structured logs (CodeQL
    /// cs/log-forging); truncate long inputs.</summary>
    internal static string? LogSafe(string? value)
    {
        if (value is null) return null;
        var clean = value.Replace('\n', ' ').Replace('\r', ' ');
        return clean.Length > 200 ? clean[..200] : clean;
    }
}
