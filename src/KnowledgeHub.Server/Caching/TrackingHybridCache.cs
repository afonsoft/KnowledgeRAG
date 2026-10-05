using Microsoft.Extensions.Caching.Hybrid;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// <see cref="HybridCache"/> decorator that mirrors every write into
/// <see cref="CacheManagerService.Current"/>'s tracked-key registry. The
/// <c>EndpointCache</c>/endpoint response-cache entries (admin screens, MCP
/// metadata, A2A card) all funnel through <see cref="HybridCache"/> — without
/// this decorator they never showed up in the Cache panel, which only listed
/// <c>SafeCache</c>-tracked keys. Serialized size isn't observable at this
/// layer (the entry is an object), so tracked keys record size 0; TTL comes
/// from <see cref="HybridCacheEntryOptions.Expiration"/>.
/// </summary>
public sealed class TrackingHybridCache : HybridCache
{
    private readonly HybridCache _inner;

    public TrackingHybridCache(HybridCache inner) => _inner = inner;

    public override async ValueTask<T> GetOrCreateAsync<TState, T>(
        string key, TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var tagList = tags?.ToArray();
        var value = await _inner.GetOrCreateAsync(key, state, factory, options, tagList, cancellationToken);
        // A hit means the entry exists; a miss just created it — either way
        // the key holds a value and belongs in the registry.
        CacheManagerService.Current?.TrackKey(key, 0, options?.Expiration, tagList);
        return value;
    }

    public override async ValueTask SetAsync<T>(
        string key, T value,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var tagList = tags?.ToArray();
        await _inner.SetAsync(key, value, options, tagList, cancellationToken);
        CacheManagerService.Current?.TrackKey(key, 0, options?.Expiration, tagList);
    }

    public override async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await _inner.RemoveAsync(key, cancellationToken);
        CacheManagerService.Current?.RemoveKey(key);
    }

    public override async ValueTask RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var list = keys as string[] ?? keys.ToArray();
        await _inner.RemoveAsync(list, cancellationToken);
        foreach (var key in list)
            CacheManagerService.Current?.RemoveKey(key);
    }

    public override async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        await _inner.RemoveByTagAsync(tag, cancellationToken);
        CacheManagerService.Current?.RemoveTag(tag);
    }

    public override async ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var list = tags as string[] ?? tags.ToArray();
        await _inner.RemoveByTagAsync(list, cancellationToken);
        foreach (var tag in list)
            CacheManagerService.Current?.RemoveTag(tag);
    }
}
