using KnowledgeHub.Server.Api;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>Covers the EndpointCache fail-soft wrappers and the
/// [AsParameters] DI surfaces extracted for S107 — code only reached by the
/// integration suite (which the coverage gate does not measure).</summary>
public sealed class EndpointCacheAndParamsTests
{
    private sealed class ThrowingHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key, TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backend down");

        public override ValueTask SetAsync<T>(
            string key, T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backend down");

        public override ValueTask RemoveAsync(
            string key, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backend down");

        public override ValueTask RemoveByTagAsync(
            string tag, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backend down");
    }

    private sealed class FakeBus : ICacheInvalidationBus
    {
        public List<string> Published { get; } = [];
        public Task PublishAsync(string topic, CancellationToken ct = default)
        {
            Published.Add(topic);
            return Task.CompletedTask;
        }
        public event EventHandler<string>? Received { add { } remove { } }
    }

    private sealed class EmptyCatalog : IDynamicToolCatalog
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
            IServiceProvider services, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CatalogTool>>([]);
        public Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(
            IServiceProvider services, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CatalogTool>>([]);
    }

    private sealed class FakeRateLimitResolver : IApiKeyRateLimitResolver
    {
        public bool TryGetOverride(Guid keyId, out ApiKeyRateLimitOverride? value)
        {
            value = null;
            return false;
        }
        public void Invalidate() { }
    }

    private static HybridCache NewCache() =>
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider()
            .GetRequiredService<HybridCache>();

    private static ILoggerFactory NewLoggerFactory() =>
        LoggerFactory.Create(_ => { });

    [Fact]
    public async Task GetJson_ReturnsFactoryValue_AndCachesSecondCall()
    {
        var cache = NewCache();
        var calls = 0;
        Task<int> Factory(CancellationToken _) { calls++; return Task.FromResult(42); }

        var first = await EndpointCache.GetJsonAsync(
            cache, "k1", Factory, NewLoggerFactory(), CancellationToken.None);
        var second = await EndpointCache.GetJsonAsync(
            cache, "k1", Factory, NewLoggerFactory(), CancellationToken.None);

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetJson_FailSoft_ServesFactoryWhenBackendThrows()
    {
        var value = await EndpointCache.GetJsonAsync(
            new ThrowingHybridCache(), "k2",
            _ => Task.FromResult<string?>("fresh"),
            NewLoggerFactory(), CancellationToken.None);
        Assert.Equal("fresh", value);
    }

    [Fact]
    public async Task Evict_RemovesKeyAndPublishesTopic()
    {
        var cache = NewCache();
        var bus = new FakeBus();
        await EndpointCache.EvictAsync(
            cache, bus, NewLoggerFactory(), CancellationToken.None, "gone");
        Assert.Equal(["cache-key:gone"], bus.Published);
    }

    [Fact]
    public async Task Evict_SwallowsBackendError_ButStillPublishes()
    {
        var bus = new FakeBus();
        await EndpointCache.EvictAsync(
            new ThrowingHybridCache(), bus, NewLoggerFactory(),
            CancellationToken.None, "gone");
        Assert.Equal(["cache-key:gone"], bus.Published);
    }

    [Fact]
    public async Task EvictTag_PublishesTagTopic()
    {
        var bus = new FakeBus();
        await EndpointCache.EvictTagAsync(
            NewCache(), bus, "scope", NewLoggerFactory(), CancellationToken.None);
        Assert.Equal(["cache-tag:scope"], bus.Published);
    }

    [Fact]
    public async Task EvictTag_SwallowsBackendError_ButStillPublishes()
    {
        var bus = new FakeBus();
        await EndpointCache.EvictTagAsync(
            new ThrowingHybridCache(), bus, "scope",
            NewLoggerFactory(), CancellationToken.None);
        Assert.Equal(["cache-tag:scope"], bus.Published);
    }

    [Fact]
    public void ApiKeyWriteParams_HoldsDependencies()
    {
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().Options);
        var dp = new EphemeralDataProtectionProvider();
        var catalog = new EmptyCatalog();
        var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = NewCache();
        var bus = new FakeBus();
        var lf = NewLoggerFactory();
        var resolver = new FakeRateLimitResolver();

        var p = new ApiKeyEndpoints.ApiKeyWriteParams
        {
            Db = db,
            DataProtection = dp,
            Catalog = catalog,
            Memory = memory,
            Cache = cache,
            Bus = bus,
            Lf = lf,
            RateLimitResolver = resolver
        };

        Assert.Same(db, p.Db);
        Assert.Same(dp, p.DataProtection);
        Assert.Same(catalog, p.Catalog);
        Assert.Same(memory, p.Memory);
        Assert.Same(cache, p.Cache);
        Assert.Same(bus, p.Bus);
        Assert.Same(lf, p.Lf);
        Assert.Same(resolver, p.RateLimitResolver);
    }

    [Fact]
    public void IntegrationWriteParams_HoldsDependencies()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var cache = NewCache();
        var bus = new FakeBus();
        var lf = NewLoggerFactory();

        var p = new SettingsEndpoints.IntegrationWriteParams
        {
            Services = services,
            Cache = cache,
            Bus = bus,
            Lf = lf
        };

        Assert.Same(services, p.Services);
        Assert.Same(cache, p.Cache);
        Assert.Same(bus, p.Bus);
        Assert.Same(lf, p.Lf);
    }
}
