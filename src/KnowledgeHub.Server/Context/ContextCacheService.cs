using System.Diagnostics;
using System.Text.Json;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Configuration;
using KnowledgeHub.Server.Telemetry;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Context;

/// <summary>
/// Dedicated HybridCache layer for <c>context_*</c> reads
/// (SPEC-20261004-context-snapshots RF-007/D-1): tag-based invalidation per
/// repo, cross-instance fan-out via <see cref="ICacheInvalidationBus"/>
/// (<c>cache-tag:</c> topics — the <see cref="InvalidationSubscriber"/>
/// mechanism already used by <see cref="EndpointCache"/>), hit reporting for
/// <c>stats.cacheHit</c> and degraded-result bypass by the caller.
/// Cache failures degrade to a plain factory call — never a 5xx.
/// </summary>
public sealed class ContextCacheService
{
    private readonly HybridCache _cache;
    private readonly ICacheInvalidationBus _bus;
    private readonly ContextOptions _options;
    private readonly int _ttlMinutes;
    private readonly ILogger<ContextCacheService> _logger;

    private static readonly TagList RegionTag = new() { { "region", "ctx" } };

    public ContextCacheService(
        HybridCache cache,
        ICacheInvalidationBus bus,
        IOptions<ContextOptions> options,
        IOptions<CacheOptions> cacheOptions,
        ILogger<ContextCacheService> logger)
    {
        _cache = cache;
        _bus = bus;
        _options = options.Value;
        _logger = logger;
        _ttlMinutes = cacheOptions.Value.RegionTtlMinutes.TryGetValue("ctx", out var ttl)
            ? ttl
            : 60;
    }

    public bool Enabled => _options.CacheEnabled;

    /// <summary>
    /// Read-through lookup with hit tracking for <c>stats.cacheHit</c> —
    /// <see cref="HybridCache.GetOrCreateAsync"/> exposes no hit flag, so we
    /// probe (factory returning null = miss sentinel), run the factory on
    /// miss and set with the repo tag. <paramref name="factory"/> returning a
    /// value implementing <see cref="IContextCacheBypass"/> with
    /// <c>BypassCache=true</c> is not stored (degraded results).
    /// </summary>
    public async Task<(T? Value, bool Hit)> GetOrCreateAsync<T>(
        string key, string repoTag,
        Func<CancellationToken, Task<T>> factory, CancellationToken ct)
    {
        if (!_options.CacheEnabled)
            return (await factory(ct), false);

        string? cached;
        try
        {
            // Negative marker: factory yields null only when the entry is
            // absent; HybridCache never stores the null long-term because a
            // real miss is followed by SetAsync below.
            cached = await _cache.GetOrCreateAsync<string?>(
                key,
                static _ => ValueTask.FromResult<string?>(null),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context cache get failed — bypassing");
            return (await factory(ct), false);
        }

        if (cached is not null)
        {
            KnowledgeHubMetrics.CacheHits.Add(1, RegionTag);
            return (JsonSerializer.Deserialize<T>(cached), true);
        }

        KnowledgeHubMetrics.CacheMisses.Add(1, RegionTag);
        var fresh = await factory(ct);
        if (fresh is not null && ShouldCache(fresh))
        {
            try
            {
                await _cache.SetAsync(
                    key,
                    JsonSerializer.Serialize(fresh),
                    new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(_ttlMinutes) },
                    [repoTag], ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Context cache set failed");
            }
        }
        return (fresh, false);
    }

    /// <summary>Drops every cached entry for the repo and fans out to other
    /// instances via <c>cache-tag:ctx:{repo}</c> on <c>kh:invalidate</c>.</summary>
    public async Task InvalidateRepoAsync(string repoKey, CancellationToken ct)
    {
        if (!_options.CacheEnabled)
            return;
        var tag = ContextCacheKeys.RepoTag(repoKey);
        try
        {
            await _cache.RemoveByTagAsync(tag, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context cache invalidation failed for {Tag}", tag);
        }
        try
        {
            await _bus.PublishAsync($"cache-tag:{tag}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context cache invalidation publish failed for {Tag}", tag);
        }
    }

    /// <summary>Pointer for the latest version (1 h TTL, refreshed on save).</summary>
    public async Task<int?> TryGetLatestVersionAsync(string repoKey, CancellationToken ct)
    {
        if (!_options.CacheEnabled)
            return null;
        try
        {
            return await _cache.GetOrCreateAsync<int?>(
                ContextCacheKeys.LatestVersionPointer(repoKey),
                static _ => ValueTask.FromResult<int?>(null),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) },
                [ContextCacheKeys.RepoTag(repoKey)], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context cache read failed");
            return null;
        }
    }

    public async Task SetLatestVersionAsync(string repoKey, int version, CancellationToken ct)
    {
        if (!_options.CacheEnabled)
            return;
        try
        {
            await _cache.SetAsync(
                ContextCacheKeys.LatestVersionPointer(repoKey),
                version,
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) },
                [ContextCacheKeys.RepoTag(repoKey)], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context cache write failed");
        }
    }

    /// <summary>Degraded responses (summaryFallback) must not be cached —
    /// convention read via interface to stay POCO-agnostic.</summary>
    private static bool ShouldCache<T>(T value) =>
        value is not IContextCacheBypass bypass || !bypass.BypassCache;
}

/// <summary>Responses implementing this skip caching when degraded.</summary>
public interface IContextCacheBypass
{
    bool BypassCache { get; }
}
