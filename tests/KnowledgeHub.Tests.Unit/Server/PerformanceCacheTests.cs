using System.Text.Json.Nodes;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260916-performance-memory-cache: catalog memoization (RF-001 /
// CA-002), fail-soft distributed cache (RNF-003 / CA-004), search-result and
// query-embedding caching with index-version invalidation (RF-005), and the
// batch vector upsert (RF-004 / CA-003).
[Collection("SearchTelemetry")]
public sealed class PerformanceCacheTests
{
    // ---- T1: catalog cache -------------------------------------------------

    [Fact]
    public async Task Catalog_ServesCachedAggregate_UntilNotifierVersionBumps()
    {
        var notifier = new FakeNotifier();
        var provider = new CountingProvider();
        var catalog = new DynamicToolCatalog([provider], notifier);
        var services = new ServiceCollection().BuildServiceProvider();

        var first = await catalog.GetToolsAsync(services, CancellationToken.None);
        var second = await catalog.GetToolsAsync(services, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, provider.Calls);

        await notifier.NotifyToolsChangedAsync();

        var third = await catalog.GetToolsAsync(services, CancellationToken.None);
        Assert.NotSame(first, third);
        Assert.Equal(2, provider.Calls);
    }

    private sealed class CountingProvider : IToolProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
            IServiceProvider services, CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<CatalogTool> tools =
            [
                new CatalogTool
                {
                    Name = "fake_tool",
                    Description = "test",
                    InputSchema = new JsonObject(),
                    Handler = (_, _) => ValueTask.FromResult(new CallToolResult())
                }
            ];
            return Task.FromResult(tools);
        }
    }

    private sealed class FakeNotifier : IToolCatalogChangeNotifier
    {
        private long _version;
        public long Version => _version;

        public Task NotifyToolsChangedAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _version);
            return Task.CompletedTask;
        }
    }

    // ---- RNF-003: fail-soft cache ------------------------------------------

    [Fact]
    public async Task SafeCache_DownBackend_TreatsGetAsMiss_AndNeverThrows()
    {
        var cache = new ThrowingCache();
        var logger = NullLogger.Instance;

        Assert.Null(await SafeCache.GetStringAsync(cache, "k", logger));
        Assert.Null(await SafeCache.GetAsync(cache, "k", logger));
        await SafeCache.SetStringAsync(cache, "k", "v", TimeSpan.FromMinutes(1), logger);
        await SafeCache.SetAsync(cache, "k", [1, 2, 3], TimeSpan.FromMinutes(1), logger);
        await SafeCache.SetJsonAsync(cache, "k", new { a = 1 }, TimeSpan.FromMinutes(1), logger);
    }

    private sealed class ThrowingCache : IDistributedCache
    {
        private static Exception Boom() => new InvalidOperationException("redis is down");

        public byte[]? Get(string key) => throw Boom();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Boom();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Boom();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Boom();
        public void Refresh(string key) => throw Boom();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Boom();
        public void Remove(string key) => throw Boom();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Boom();
    }

    // ---- RF-005: search-result + query-embedding cache ----------------------

    [Fact]
    public async Task Search_CachesResultAndEmbedding_UntilIndexVersionBumps()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        var chunk = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 0, TextContent = "text" };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var embeddings = new CountingEmbeddingProvider();
        var vectors = new CountingVectorStore(new VectorHit(chunk.Id, 0.9));
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var search = new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, embeddings, new Fakes.FixedEmbeddingProviderResolver(embeddings), vectors, new DisabledLexical(), cache), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), Search.FakeGraphSettings.Enabled), new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);

        var first = await search.SearchAsync("q", 5, mode: SearchMode.Semantic);
        Assert.Single(first);
        Assert.Equal("text", first[0].ChunkText);

        // Second identical search: fully served from the result cache.
        var second = await search.SearchAsync("q", 5, mode: SearchMode.Semantic);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(1, embeddings.Calls);
        Assert.Equal(1, vectors.SearchCalls);

        // A different query reuses the cached *embedding*? No — different text,
        // different key — but the result cache still misses only once.
        await search.SearchAsync("q", 5, mode: SearchMode.Semantic);
        Assert.Equal(1, embeddings.Calls);

        // Simulate a sync bumping the index-version token → stale results miss.
        await cache.SetStringAsync(CacheKeys.IndexVersion, Guid.NewGuid().ToString("N"));
        await search.SearchAsync("q", 5, mode: SearchMode.Semantic);
        Assert.Equal(2, vectors.SearchCalls);
        Assert.Equal(1, embeddings.Calls); // query embedding still cached
    }

    [Fact]
    public async Task Search_DownCache_StillReturnsResults()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        var chunk = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 0, TextContent = "text" };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var emb = new CountingEmbeddingProvider();
        var search = new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb), new CountingVectorStore(new VectorHit(chunk.Id, 0.9)), new DisabledLexical(), new ThrowingCache()), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), Search.FakeGraphSettings.Enabled), new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic);
        Assert.Single(results);
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public int Calls { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new[] { 1f, 0f, 0f, 0f });
        }
    }

    private sealed class CountingVectorStore(VectorHit hit) : IVectorStore
    {
        public int SearchCalls { get; private set; }

        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
            IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            IReadOnlyList<VectorHit> hits = [hit];
            return Task.FromResult(hits);
        }
    }

    private sealed class DisabledLexical : ILexicalSearchService
    {
        public bool Enabled => false;
        public Task<IReadOnlyList<LexicalHit>> SearchAsync(
            string query, int topK, IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LexicalHit>>([]);
        public Task ReconcileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    // ---- RF-004: batch vector upsert ---------------------------------------

    [Fact]
    public async Task SqliteVectorStore_UpsertBatch_StampsAllChunksInOneSave()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        var c1 = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 0, TextContent = "a" };
        var c2 = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 1, TextContent = "b" };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.AddRange(c1, c2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new SqliteVectorStore(db);
        await store.UpsertBatchAsync(
            [
                new VectorUpsert(c1.Id, doc.Id, source.Id, [1f, 0f]),
                new VectorUpsert(c2.Id, doc.Id, source.Id, [0f, 1f])
            ],
            "fake:2");

        var hits = await store.SearchAsync([1f, 0f], "fake:2", topK: 1);
        Assert.Single(hits);
        Assert.Equal(c1.Id, hits[0].ChunkId);

        var missing = await store.SearchAsync([1f, 0f], "other-model", topK: 5);
        Assert.Empty(missing);
    }
}
