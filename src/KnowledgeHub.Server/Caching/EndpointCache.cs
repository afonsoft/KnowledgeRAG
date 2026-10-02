using Microsoft.Extensions.Caching.Hybrid;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// Endpoint-level response caching on <see cref="HybridCache"/> — L1 (in-process
/// object) → L2 (the configured <c>IDistributedCache</c>, Redis when
/// <c>Cache:Provider=redis</c>) for the read endpoints backing the admin
/// screens, MCP metadata and the A2A agent card. Stampede protection is
/// built in. Fail-soft wrapper: a Redis outage degrades to a direct factory
/// call, never a 5xx. Mutations invalidate via tags (<c>RemoveByTagAsync</c>
/// on every replica through the invalidation bus — HybridCache alone does not
/// clear the remote L1s). Only anonymous-equivalent/global payloads belong
/// here — never per-user data under a shared key.
/// </summary>
public static class EndpointCache
{
    /// <summary>Get-or-create under an exact key. TTL omitted → region policy
    /// via <see cref="CacheTtlPolicy"/>; L1 lifetime is capped by
    /// <c>Cache:L1MaxTtlMinutes</c> (or disabled by <c>Cache:L1Enabled=false</c>).</summary>
    public static Task<T?> GetJsonAsync<T>(
        HybridCache cache, string key,
        Func<CancellationToken, Task<T?>> factory,
        ILoggerFactory loggerFactory, CancellationToken ct,
        TimeSpan? ttl = null, IEnumerable<string>? tags = null) =>
        GetJsonAsync(cache, key, factory,
            loggerFactory.CreateLogger(typeof(EndpointCache)), ct, ttl, tags);

    /// <inheritdoc cref="GetJsonAsync{T}(HybridCache, string, Func{CancellationToken, Task{T?}}, ILoggerFactory, CancellationToken, TimeSpan?, IEnumerable{string}?)"/>
    public static async Task<T?> GetJsonAsync<T>(
        HybridCache cache, string key,
        Func<CancellationToken, Task<T?>> factory,
        ILogger logger, CancellationToken ct,
        TimeSpan? ttl = null, IEnumerable<string>? tags = null)
    {
        var options = EntryOptions(key, ttl);
        try
        {
            return await cache.GetOrCreateAsync(key,
                async c => await factory(c), options, tags, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "endpoint cache {Key} failed — serving fresh", LogSafe(key));
            return await factory(ct);
        }
    }

    /// <summary>Drop keys on this replica and broadcast <c>cache-key:{key}</c>
    /// so the others evict their L1 copies.</summary>
    public static Task EvictAsync(
        HybridCache cache, ICacheInvalidationBus? bus,
        ILoggerFactory loggerFactory, CancellationToken ct, params string[] keys) =>
        EvictAsync(cache, bus, loggerFactory.CreateLogger(typeof(EndpointCache)), ct, keys);

    /// <inheritdoc cref="EvictAsync(HybridCache, ICacheInvalidationBus?, ILoggerFactory, CancellationToken, string[])"/>
    public static async Task EvictAsync(
        HybridCache cache, ICacheInvalidationBus? bus,
        ILogger logger, CancellationToken ct, params string[] keys)
    {
        foreach (var key in keys)
        {
            try { await cache.RemoveAsync(key, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "endpoint cache evict {Key} failed", LogSafe(key));
            }
            if (bus is not null)
                await bus.PublishAsync($"cache-key:{key}", ct);
        }
    }

    /// <summary>Invalidate a whole tag scope — logical invalidation in L2 plus
    /// a <c>cache-tag:{tag}</c> broadcast so remote L1s drop the stamped
    /// entries now rather than at TTL.</summary>
    public static Task EvictTagAsync(
        HybridCache cache, ICacheInvalidationBus? bus, string tag,
        ILoggerFactory loggerFactory, CancellationToken ct) =>
        EvictTagAsync(cache, bus, tag, loggerFactory.CreateLogger(typeof(EndpointCache)), ct);

    /// <inheritdoc cref="EvictTagAsync(HybridCache, ICacheInvalidationBus?, string, ILoggerFactory, CancellationToken)"/>
    public static async Task EvictTagAsync(
        HybridCache cache, ICacheInvalidationBus? bus, string tag,
        ILogger logger, CancellationToken ct)
    {
        try { await cache.RemoveByTagAsync(tag, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "endpoint cache tag evict {Tag} failed", LogSafe(tag));
        }
        if (bus is not null)
            await bus.PublishAsync($"cache-tag:{tag}", ct);
    }

    // keys/tags are caller- and channel-originated — strip line breaks before
    // they reach the rendered log message (log forging).
    private static string LogSafe(string value) => value.ReplaceLineEndings("_");

    private static HybridCacheEntryOptions EntryOptions(string key, TimeSpan? ttl)
    {
        var effective = ttl ?? CacheTtlPolicy.Current?.For(key) ?? TimeSpan.FromMinutes(10);
        var l1Cap = CacheTtlPolicy.Current?.L1Cap;
        return new HybridCacheEntryOptions
        {
            Expiration = effective,
            LocalCacheExpiration = l1Cap is { } cap && cap < effective ? cap : effective,
            Flags = l1Cap == TimeSpan.Zero ? HybridCacheEntryFlags.DisableLocalCache : default
        };
    }
}
