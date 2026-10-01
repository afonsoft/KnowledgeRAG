using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// SPEC-20260925-hybrid-cache-l1l2 + SPEC-20260925-cache-region-ttl-policies.
/// </summary>
public sealed class HybridCacheAndTtlTests
{
    private sealed class CountingL2(MemoryDistributedCache inner) : IDistributedCache
    {
        public int Gets;
        private readonly MemoryDistributedCache _inner = inner;
        public byte[]? Get(string key) => throw new NotImplementedException();
        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Gets++;
            return await _inner.GetAsync(key, token);
        }
        public void Set(string k, byte[] v, DistributedCacheEntryOptions o) => throw new NotImplementedException();
        public Task SetAsync(string k, byte[] v, DistributedCacheEntryOptions o, CancellationToken t = default)
            => _inner.SetAsync(k, v, o, t);
        public void Refresh(string k) { }
        public Task RefreshAsync(string k, CancellationToken t = default) => Task.CompletedTask;
        public void Remove(string k) { }
        public Task RemoveAsync(string k, CancellationToken t = default) => _inner.RemoveAsync(k, t);
    }

    [Fact]
    public async Task L1_ShortCircuits_L2AfterFirstRead()
    {
        var l2 = new CountingL2(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var cache = new L1L2Cache(new MemoryCache(new MemoryCacheOptions()), l2, TimeSpan.FromMinutes(5));

        await l2.SetAsync("k", [1, 2, 3], new DistributedCacheEntryOptions());

        // First read populates L1; second read never touches L2.
        var v1 = await cache.GetAsync("k");
        var v2 = await cache.GetAsync("k");
        Assert.Equal(new byte[] { 1, 2, 3 }, v1);
        Assert.Equal(v1, v2);
        Assert.Equal(1, l2.Gets); // exactly one real fetch — second read hit L1
    }

    [Fact]
    public async Task Remove_EvictsBothTiers()
    {
        var l2 = new CountingL2(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var cache = new L1L2Cache(new MemoryCache(new MemoryCacheOptions()), l2, TimeSpan.FromMinutes(5));
        await cache.SetAsync("k", [9], new DistributedCacheEntryOptions());
        await cache.RemoveAsync("k");
        Assert.Null(await cache.GetAsync("k"));
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentMiss_FactoryRunsOnce()
    {
        var l2 = new CountingL2(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var cache = new L1L2Cache(new MemoryCache(new MemoryCacheOptions()), l2, TimeSpan.FromMinutes(5));

        var calls = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ =>
            SafeCache.GetOrCreateAsync<string>(cache, "hot",
                async ct => { Interlocked.Increment(ref calls); await Task.Delay(50, ct); return "v"; },
                new SafeCache.CacheCodec<string>(
                    v => System.Text.Encoding.UTF8.GetBytes(v),
                    b => System.Text.Encoding.UTF8.GetString(b)),
                TimeSpan.FromMinutes(1),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("v", r));
        Assert.Equal(1, calls); // stampede collapsed
    }

    [Fact]
    public void TtlPolicy_RegionPrefixMatch()
    {
        var policy = new CacheTtlPolicy(Options.Create(new CacheOptions()));
        Assert.Equal(TimeSpan.FromMinutes(1440), policy.For("emb:m:h"));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.For("search:v3:x"));
        Assert.Equal(TimeSpan.FromMinutes(10), policy.For("ans:m:h"));
        Assert.Equal(TimeSpan.FromMinutes(60), policy.For("mcp:tool:t:h"));
        Assert.Equal(TimeSpan.FromMinutes(10), policy.For("unknown:key")); // default
    }

    [Fact]
    public void TtlPolicy_OverrideViaOptions()
    {
        var policy = new CacheTtlPolicy(Options.Create(new CacheOptions
        {
            RegionTtlMinutes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["search"] = 1,
                ["emb"] = 2
            }
        }));
        Assert.Equal(TimeSpan.FromMinutes(1), policy.For("search:x"));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.For("emb:x"));
    }
}
