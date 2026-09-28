using System.Collections.Concurrent;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Search;

// Covers SPEC-20260927-hierarchical-filter-relaxation-and-multiquery:
// scope cascade (source→type→global), isRelaxed flags, strict precedence and
// caller-supplied sub-query fusion.
[Collection("SearchTelemetry")]
public sealed class HierarchicalRelaxationTests
{
    private static async Task<(SqliteConnection conn, KnowledgeHubDbContext db,
            KnowledgeSource sourceA, KnowledgeSource sourceB, DocumentChunk chunkB)>
        SeedAsync(SourceType typeB = SourceType.DocumentFile)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        // A: WebPage source with NO chunks; B: source with one matching chunk.
        var sourceA = new KnowledgeSource { Name = "a", SourceType = SourceType.WebPage, IsActive = true };
        var sourceB = new KnowledgeSource { Name = "b", SourceType = typeB, IsActive = true };
        var docA = new KnowledgeDocument { Title = "da", UriReference = "a/x", KnowledgeSourceId = sourceA.Id };
        var docB = new KnowledgeDocument { Title = "db", UriReference = "b/y", KnowledgeSourceId = sourceB.Id };
        var chunkB = new DocumentChunk
        {
            KnowledgeDocumentId = docB.Id,
            ChunkIndex = 0,
            TextContent = "shared-topic text"
        };
        db.Sources.AddRange(sourceA, sourceB);
        db.Documents.AddRange(docA, docB);
        db.Chunks.Add(chunkB);
        await db.SaveChangesAsync();
        return (conn, db, sourceA, sourceB, chunkB);
    }

    private static SearchService NewSearch(KnowledgeHubDbContext db,
        IReadOnlyList<VectorHit> hits, IReadOnlyDictionary<Guid, Guid> chunkSource,
        IConfiguration? config = null)
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new StubEmbeddings();
        return new SearchService(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb),
            new ScopedVectorStore(hits, chunkSource), new DisabledLexical(), cache,
            config ?? new ConfigurationBuilder().Build(), new PassthroughRewriter(),
            NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            NoOpReranker.Instance, new UnrestrictedScope(), FakeGraphSettings.Disabled,
            NullLogger<SearchService>.Instance);
    }

    private static SearchService NewRoutedSearch(KnowledgeHubDbContext db,
        Func<string, IReadOnlyList<VectorHit>> route)
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new RoutedEmbeddings();
        return new SearchService(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb),
            new RoutedVectorStore(emb, route), new DisabledLexical(), cache,
            new ConfigurationBuilder().Build(), new PassthroughRewriter(),
            NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            NoOpReranker.Instance, new UnrestrictedScope(), FakeGraphSettings.Disabled,
            NullLogger<SearchService>.Instance);
    }

    [Fact]
    public async Task StrictSource_NoMatches_RelaxesToGlobal_AndFlagsHits()
    {
        // AC-2: sourceId scope with no chunks → falls back, flags isRelaxed.
        var (conn, db, sourceA, sourceB, chunkB) = await SeedAsync();
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, [new VectorHit(chunkB.Id, 0.9)],
            new Dictionary<Guid, Guid> { [chunkB.Id] = sourceB.Id });

        var results = await search.SearchAsync("q", 5, sourceId: sourceA.Id, mode: SearchMode.Semantic);

        var item = Assert.Single(results);
        Assert.True(item.IsRelaxed);
        Assert.Equal("global", item.RelaxedScope);
        Assert.Equal(chunkB.Id, item.ChunkId);
    }

    [Fact]
    public async Task SameTypeSource_RelaxesAtLevel2_TypeScope()
    {
        // B same type as A → level 2 (sourceType) catches it before global.
        var (conn, db, sourceA, sourceB, chunkB) = await SeedAsync(SourceType.WebPage);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, [new VectorHit(chunkB.Id, 0.9)],
            new Dictionary<Guid, Guid> { [chunkB.Id] = sourceB.Id });

        var results = await search.SearchAsync("q", 5, sourceId: sourceA.Id, mode: SearchMode.Semantic);

        var item = Assert.Single(results);
        Assert.True(item.IsRelaxed);
        Assert.Equal("sourceType=WebPage", item.RelaxedScope);
    }

    [Fact]
    public async Task AllowRelaxationFalse_StrictEmpty_ReturnsEmpty()
    {
        // AC-3: opt-out honoured — zero results, no fallback.
        var (conn, db, sourceA, sourceB, chunkB) = await SeedAsync();
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, [new VectorHit(chunkB.Id, 0.9)],
            new Dictionary<Guid, Guid> { [chunkB.Id] = sourceB.Id });
        var filter = new ResolvedSearchFilter(null, null, null, null, AllowRelaxation: false);

        var results = await search.SearchAsync("q", 5, sourceId: sourceA.Id,
            mode: SearchMode.Semantic, filter: filter);

        Assert.Empty(results);
    }

    [Fact]
    public async Task StrictPrecedence_RelaxedHitsRankBelow()
    {
        // AC-4: strict hits keep precedence — relaxed score × 0.85^level.
        var (conn, db, sourceA, sourceB, chunkB) = await SeedAsync(SourceType.WebPage);
        await using var _c = conn; await using var _d = db;
        // Strict source A gets one chunk; MinResults=3 forces the cascade to merge.
        var docA = db.Documents.Single(d => d.KnowledgeSourceId == sourceA.Id);
        var chunkA = new DocumentChunk
        {
            KnowledgeDocumentId = docA.Id,
            ChunkIndex = 0,
            TextContent = "strict hit"
        };
        db.Chunks.Add(chunkA);
        await db.SaveChangesAsync();

        var chunkSource = new Dictionary<Guid, Guid>
        {
            [chunkA.Id] = sourceA.Id,
            [chunkB.Id] = sourceB.Id
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Search:Relaxation:MinResults"] = "3"
            })
            .Build();
        var search = NewSearch(db,
            [new VectorHit(chunkA.Id, 0.9), new VectorHit(chunkB.Id, 0.9)], chunkSource, config);

        var results = await search.SearchAsync("q", 5, sourceId: sourceA.Id, mode: SearchMode.Semantic);

        Assert.Equal(2, results.Count);
        Assert.Equal(chunkA.Id, results[0].ChunkId);
        Assert.False(results[0].IsRelaxed);
        Assert.Equal(chunkB.Id, results[1].ChunkId);
        Assert.True(results[1].IsRelaxed);
        Assert.True(results[0].Score > results[1].Score); // same raw score, relaxed penalized
    }

    [Fact]
    public async Task SubQueries_FuseExtraHits_WithoutDuplicates()
    {
        // AC-1: caller sub-queries dispatch parallel arms and merge deduplicated.
        var (conn, db, sourceA, _, chunkB) = await SeedAsync();
        await using var _c = conn; await using var _d = db;
        var docA = db.Documents.Single(d => d.KnowledgeSourceId == sourceA.Id);
        var chunkA = new DocumentChunk
        {
            KnowledgeDocumentId = docA.Id,
            ChunkIndex = 0,
            TextContent = "main hit"
        };
        db.Chunks.Add(chunkA);
        await db.SaveChangesAsync();

        // "main" → chunkA, "sub-term" → chunkB, both deduped into one ranking.
        var search = NewRoutedSearch(db, q =>
            q == "sub-term" ? [new VectorHit(chunkB.Id, 0.8)] : [new VectorHit(chunkA.Id, 0.9)]);
        var filter = new ResolvedSearchFilter(null, null, null, null,
            SubQueries: ["sub-term", "", "   "]); // blanks must be filtered out

        var results = await search.SearchAsync("main", 5, mode: SearchMode.Semantic, filter: filter);

        Assert.Equal(2, results.Count);
        Assert.Equal(new[] { chunkA.Id, chunkB.Id }.OrderBy(x => x).ToArray(),
            results.Select(r => r.ChunkId!.Value).OrderBy(x => x).ToArray());
    }

    private sealed class StubEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
    }

    /// <summary>Embeddings whose first dimension encodes a per-text key, so the
    /// routed store can map vectors back to query text (race-safe).</summary>
    private sealed class RoutedEmbeddings : IEmbeddingProvider
    {
        public readonly ConcurrentDictionary<int, string> ByKey = new();
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var key = Math.Abs(text.GetHashCode() % 1000);
            ByKey[key] = text;
            return Task.FromResult(new[] { (float)key, 0f, 0f, 0f });
        }
    }

    /// <summary>Returns only hits whose chunk belongs to an active source
    /// (mirrors the real store's sourceIds filter — needed for the cascade).</summary>
    private sealed class ScopedVectorStore(
        IReadOnlyList<VectorHit> hits, IReadOnlyDictionary<Guid, Guid> chunkSource) : IVectorStore
    {
        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector,
            string model, IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default)
        {
            var filtered = hits
                .Where(h => chunkSource.TryGetValue(h.ChunkId, out var sid)
                    && (sourceIds is null || sourceIds.Contains(sid)))
                .Take(topK).ToList();
            return Task.FromResult<IReadOnlyList<VectorHit>>(filtered);
        }
    }

    /// <summary>Routes by embedding vector[0] — the RoutedEmbeddings key encodes
    /// which query text produced the vector.</summary>
    private sealed class RoutedVectorStore(
        RoutedEmbeddings emb, Func<string, IReadOnlyList<VectorHit>> route) : IVectorStore
    {
        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector,
            string model, IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorHit>>(
                route(emb.ByKey[(int)queryVector[0]]).Take(topK).ToList());
    }

    private sealed class DisabledLexical : ILexicalSearchService
    {
        public bool Enabled => false;
        public Task<IReadOnlyList<LexicalHit>> SearchAsync(string query, int topK,
            IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken = default) =>
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
}
