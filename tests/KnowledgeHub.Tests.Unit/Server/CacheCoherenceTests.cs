using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Configuration;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260926-cache-coherence-and-ttl: striped locks, L1 on L2 write
// failure, remote cache-clear dropping tracked L2 keys, and degraded vector-arm
// results never entering the search cache.
[Collection("SearchTelemetry")]
public sealed class CacheCoherenceTests
{
    // ---- RF-002: striped locks ----

    [Fact]
    public void L1L2_LockFor_IsBounded_Striped()
    {
        var cache = NewL1L2();
        var seen = new HashSet<SemaphoreSlim>();
        for (var i = 0; i < 5000; i++)
            seen.Add(cache.LockFor($"emb:k{i}"));
        Assert.True(seen.Count <= 256);
        Assert.Same(cache.LockFor("same"), cache.LockFor("same"));
    }

    private static L1L2Cache NewL1L2(IDistributedCache? l2 = null) =>
        new(new MemoryCache(new MemoryCacheOptions()),
            l2 ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            TimeSpan.FromMinutes(1), NullLogger<L1L2Cache>.Instance);

    // ---- RF-006: L2 write failure still caches L1 ----

    private sealed class FailOnSetCache(IDistributedCache inner) : IDistributedCache
    {
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken ct = default) =>
            throw new InvalidOperationException("redis down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException();
        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);
        public byte[]? Get(string key) => inner.Get(key);
        public Task RefreshAsync(string key, CancellationToken ct = default) => inner.RefreshAsync(key, ct);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RemoveAsync(string key, CancellationToken ct = default) => inner.RemoveAsync(key, ct);
        public void Remove(string key) => inner.Remove(key);
    }

    [Fact]
    public async Task L1L2_SetAsync_L2Down_StillPopulatesL1()
    {
        var backing = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cache = NewL1L2(new FailOnSetCache(backing));
        await cache.SetAsync("k", "v"u8.ToArray(), new DistributedCacheEntryOptions());
        Assert.Equal("v"u8.ToArray(), await cache.GetAsync("k")); // served from L1
    }

    // ---- RF-004: remote cache-clear drops this node's L2 keys ----

    private sealed class FakeBus : ICacheInvalidationBus
    {
        public event EventHandler<string>? Received;
        public Task PublishAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
        public void Fire(string topic) => Received?.Invoke(this, topic);
    }

    private sealed class RecordingManager : ICacheManagerService
    {
        public int Clears { get; private set; }
        public Task<CacheStatsDto> GetStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(new CacheStatsDto());
        public Task<ClearCacheResultDto> ClearAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new ClearCacheResultDto());
        public Task<CacheKeyRemovalResult> RemoveEntryAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(new CacheKeyRemovalResult { Tracked = false, Removed = false });
        public void TrackKey(string key, long sizeBytes, TimeSpan? ttl = null, IEnumerable<string>? tags = null) { }
        public void RemoveKey(string key) { }
        public void RemoveTag(string tag) { }
        public void RecordHit(bool hit) { }
        public Task ClearLocalTrackedAsync(CancellationToken ct = default)
        {
            Clears++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RemoteCacheClear_ClearsL1_AndOwnL2Keys()
    {
        var bus = new FakeBus();
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l1l2 = new L1L2Cache(l1,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            TimeSpan.FromMinutes(1), NullLogger<L1L2Cache>.Instance);
        await l1l2.SetAsync("hot", "v"u8.ToArray(), new DistributedCacheEntryOptions());
        var manager = new RecordingManager();
        using var sub = new InvalidationSubscriber(bus, l1l2, NullLogger<InvalidationSubscriber>.Instance, manager);

        bus.Fire("cache-clear");
        for (var i = 0; i < 100 && manager.Clears == 0; i++)
            await Task.Delay(20); // L2 cleanup is fire-and-forget — poll, don't flake

        Assert.Equal(1, manager.Clears);
        Assert.Null(l1.Get("hot")); // L1 compacted — L2 path is exercised via manager
    }

    // ---- RF-005: degraded vector-arm result is not cached ----

    private sealed class ThrowingVectorStore : IVectorStore
    {
        public int Calls { get; private set; }
        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
            IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("vector store down");
        }
    }

    private sealed class HitLexical : ILexicalSearchService
    {
        public bool Enabled => true;
        public Task<IReadOnlyList<LexicalHit>> SearchAsync(string query, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LexicalHit>>([new LexicalHit(Guid.NewGuid(), 0, 1.0)]);
        public Task ReconcileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingCache(IDistributedCache inner) : IDistributedCache
    {
        public List<string> Sets { get; } = [];
        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);
        public byte[]? Get(string key) => inner.Get(key);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken ct = default)
        {
            Sets.Add(key);
            return inner.SetAsync(key, value, options, ct);
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { Sets.Add(key); inner.Set(key, value, options); }
        public Task RefreshAsync(string key, CancellationToken ct = default) => inner.RefreshAsync(key, ct);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RemoveAsync(string key, CancellationToken ct = default) => inner.RemoveAsync(key, ct);
        public void Remove(string key) => inner.Remove(key);
    }

    private sealed class StubEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
    }

    private sealed class PassthroughRewriter : IQueryRewriter
    {
        public Task<string> RewriteAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(query);
    }

    private sealed class UnrestrictedScope : KnowledgeHub.Server.Auth.ICallerScopeProvider
    {
        public Task<KnowledgeHub.Server.Auth.CallerScope> GetAsync(CancellationToken ct) =>
            Task.FromResult(KnowledgeHub.Server.Auth.CallerScope.Unrestricted);
    }

    [Fact]
    public async Task Search_DegradedVectorArm_NotCached()
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();
        // A source must exist — with zero active sources the pipeline returns
        // early and the vector arm is never exercised.
        db.Sources.Add(new KnowledgeSource
        {
            Name = "s",
            SourceType = SourceType.ObsidianVault,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var emb = new StubEmbeddings();
        var vectors = new ThrowingVectorStore();
        var backing = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cache = new RecordingCache(backing);
        var search = new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, emb, new KnowledgeHub.Tests.Unit.Fakes.FixedEmbeddingProviderResolver(emb), vectors, new HitLexical(), cache), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), KnowledgeHub.Tests.Unit.Search.FakeGraphSettings.Disabled), new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);

        await search.SearchAsync("q", 5, mode: SearchMode.Hybrid);
        await search.SearchAsync("q", 5, mode: SearchMode.Hybrid); // re-executes — no cache

        Assert.Equal(2, vectors.Calls); // second call hit the store again — nothing was cached
        Assert.DoesNotContain(cache.Sets, k => k.StartsWith("search:", StringComparison.Ordinal));
    }
}
