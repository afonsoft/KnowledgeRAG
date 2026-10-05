using System.Collections.Concurrent;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// Choke-point key tracking: <see cref="L1L2Cache"/> and the
/// <see cref="TrackingHybridCache"/> decorator record every write in
/// <see cref="CacheManagerService.Current"/> so the Cache panel lists keys
/// written via direct <c>IDistributedCache</c> callers and
/// <c>EndpointCache</c>/<see cref="HybridCache"/> paths — not just SafeCache.
/// </summary>
public sealed class CacheKeyTrackingTests
{
    private sealed class FakeHybridCache : HybridCache
    {
        public readonly ConcurrentDictionary<string, object?> Store = new(StringComparer.Ordinal);

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key, TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            if (Store.TryGetValue(key, out var existing) && existing is T typed)
                return typed;
            var produced = await factory(state, cancellationToken);
            Store[key] = produced;
            return produced;
        }

        public override ValueTask SetAsync<T>(string key, T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            Store[key] = value;
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Store.TryRemove(key, out _);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private static CacheManagerService MakeManager() =>
        new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            Options.Create(new CacheOptions { Provider = "memory" }),
            NullLogger<CacheManagerService>.Instance);

    private static L1L2Cache MakeL1L2() =>
        new(new MemoryCache(new MemoryCacheOptions()),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            TimeSpan.FromMinutes(5));

    // Assert only on OUR keys — the static Current is shared across parallel
    // tests (same convention as CacheManagerServiceTests).
    private const string Prefix = "trk:";

    [Fact]
    public async Task L1L2_SetAsync_TracksKeyWithSizeAndTtl()
    {
        var manager = MakeManager();
        var cache = MakeL1L2();

        await cache.SetAsync(Prefix + "l1l2", "payload"u8.ToArray(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });

        var stats = await manager.GetStatsAsync();
        var item = Assert.Single(stats.Keys, k => k.Key == Prefix + "l1l2");
        Assert.Equal("payload"u8.ToArray().Length, item.SizeBytes);
        Assert.NotNull(item.ExpiresInSeconds);
        Assert.InRange(item.ExpiresInSeconds!.Value, 1780, 1805);
    }

    [Fact]
    public async Task L1L2_SetAsync_WithoutTtl_TracksWithNullExpiry()
    {
        var manager = MakeManager();
        var cache = MakeL1L2();

        await cache.SetAsync(Prefix + "nottl", [1, 2], new DistributedCacheEntryOptions());

        var stats = await manager.GetStatsAsync();
        var item = Assert.Single(stats.Keys, k => k.Key == Prefix + "nottl");
        Assert.Null(item.ExpiresInSeconds);
    }

    [Fact]
    public async Task L1L2_RemoveAsync_UntracksKey()
    {
        var manager = MakeManager();
        var cache = MakeL1L2();
        await cache.SetAsync(Prefix + "gone", [1], new DistributedCacheEntryOptions());

        await cache.RemoveAsync(Prefix + "gone");

        var stats = await manager.GetStatsAsync();
        Assert.DoesNotContain(stats.Keys, k => k.Key == Prefix + "gone");
    }

    [Fact]
    public async Task SafeCache_Write_StillTracks_Once_NotTwice()
    {
        var manager = MakeManager();
        var cache = MakeL1L2();

        await SafeCache.SetStringAsync(cache, Prefix + "dedupe", "hello",
            TimeSpan.FromMinutes(5), NullLogger.Instance);

        var stats = await manager.GetStatsAsync();
        var item = Assert.Single(stats.Keys, k => k.Key == Prefix + "dedupe");
        Assert.Equal(5, item.SizeBytes); // "hello" — SafeCache byte count wins, same value either way
    }

    [Fact]
    public async Task Hybrid_GetOrCreate_TracksKeyWithTags()
    {
        var manager = MakeManager();
        var inner = new FakeHybridCache();
        var cache = new TrackingHybridCache(inner);

        var v = await cache.GetOrCreateAsync<string, string>(Prefix + "ep", "s",
            static async (_, _) => await ValueTask.FromResult("page"),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) },
            ["settings"]);

        Assert.Equal("page", v);
        var stats = await manager.GetStatsAsync();
        var item = Assert.Single(stats.Keys, k => k.Key == Prefix + "ep");
        Assert.Equal("tracked", item.Source);
        Assert.Equal("settings", Assert.Single(item.Tags!));
        Assert.NotNull(item.ExpiresInSeconds);
    }

    [Fact]
    public async Task Hybrid_RemoveAsync_UntracksKey()
    {
        var manager = MakeManager();
        var cache = new TrackingHybridCache(new FakeHybridCache());
        await cache.SetAsync(Prefix + "rm", "v");

        await cache.RemoveAsync(Prefix + "rm");

        var stats = await manager.GetStatsAsync();
        Assert.DoesNotContain(stats.Keys, k => k.Key == Prefix + "rm");
    }

    [Fact]
    public async Task Hybrid_RemoveByTag_UntracksOnlyTaggedKeys()
    {
        var manager = MakeManager();
        var cache = new TrackingHybridCache(new FakeHybridCache());
        await cache.SetAsync(Prefix + "a", "v", tags: ["settings"]);
        await cache.SetAsync(Prefix + "b", "v", tags: ["settings", "admin"]);
        await cache.SetAsync(Prefix + "c", "v", tags: ["other"]);

        await cache.RemoveByTagAsync("settings");

        var stats = await manager.GetStatsAsync();
        Assert.DoesNotContain(stats.Keys, k => k.Key == Prefix + "a");
        Assert.DoesNotContain(stats.Keys, k => k.Key == Prefix + "b");
        Assert.Contains(stats.Keys, k => k.Key == Prefix + "c");
    }

    [Fact]
    public async Task Manager_RemoveTag_DropsOnlyTaggedEntries()
    {
        var manager = MakeManager();
        manager.TrackKey(Prefix + "x", 1, tags: ["a", "b"]);
        manager.TrackKey(Prefix + "y", 1, tags: ["b"]);

        manager.RemoveTag("b");

        var stats = await manager.GetStatsAsync();
        Assert.DoesNotContain(stats.Keys, k => k.Key == Prefix + "x");
        Assert.DoesNotContain(stats.Keys, k => k.Key == Prefix + "y");
    }
}
