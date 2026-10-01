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

// Covers SPEC-20260927-chunk-window-retrieval-and-autocut RF-002 + AC-1/AC-4:
// window stitching with neighbour indices, W=0 no-op, the 80% score gate and
// document-boundary edges.
[Collection("SearchTelemetry")]
public sealed class WindowExpansionTests
{
    private static async Task<(SqliteConnection conn, KnowledgeHubDbContext db, List<DocumentChunk> chunks)>
        SeedAsync(int count = 5)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        var chunks = Enumerable.Range(0, count)
            .Select(i => new DocumentChunk
            {
                KnowledgeDocumentId = doc.Id,
                ChunkIndex = i,
                TextContent = $"chunk-{i} text"
            })
            .ToList();
        db.Chunks.AddRange(chunks);
        await db.SaveChangesAsync();
        return (conn, db, chunks);
    }

    private static SearchService NewSearch(KnowledgeHubDbContext db, IReadOnlyList<VectorHit> hits,
        IConfiguration? config = null)
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new StubEmbeddings();
        return new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb), new StubVectorStore(hits), new DisabledLexical(), cache), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), FakeGraphSettings.Disabled), config ?? new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);
    }

    [Fact]
    public async Task WindowExpansion_StitchesNeighbours()
    {
        // AC-1: hit on chunk #2, windowSize=1 → context = chunks #1+#3.
        var (conn, db, chunks) = await SeedAsync(5);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, [new VectorHit(chunks[2].Id, 0.9)]);
        var filter = new ResolvedSearchFilter(null, null, null, null, WindowSize: 1);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        var item = Assert.Single(results);
        Assert.True(item.WindowExpanded);
        Assert.Equal([1, 3], item.ExpandedChunkIndices);
        Assert.Contains("chunk-1 text", item.Context);
        Assert.Contains("chunk-3 text", item.Context);
        Assert.DoesNotContain("chunk-0 text", item.Context!);
    }

    [Fact]
    public async Task WindowSizeZero_NoExpansion()
    {
        // AC-4: windowSize=0 → no neighbour fetch, hit preserved intact.
        var (conn, db, chunks) = await SeedAsync(3);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, [new VectorHit(chunks[1].Id, 0.9)]);
        var filter = new ResolvedSearchFilter(null, null, null, null, WindowSize: 0);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        var item = Assert.Single(results);
        Assert.False(item.WindowExpanded);
        Assert.Null(item.Context);
        Assert.Null(item.ExpandedChunkIndices);
    }

    [Fact]
    public async Task WindowExpansion_SkipsLowScoreHits()
    {
        // RF-002: only hits ≥80% of the top score earn a window.
        var (conn, db, chunks) = await SeedAsync(5);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db,
            [new VectorHit(chunks[2].Id, 0.9), new VectorHit(chunks[4].Id, 0.4)]);
        var filter = new ResolvedSearchFilter(null, null, null, null, ContextExpand: "window");

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        Assert.Equal(2, results.Count);
        var strong = results.Single(r => r.ChunkId == chunks[2].Id);
        var weak = results.Single(r => r.ChunkId == chunks[4].Id);
        Assert.True(strong.WindowExpanded);
        Assert.False(weak.WindowExpanded); // 0.4/0.9 = 44% < 80% gate
    }

    [Fact]
    public async Task WindowExpansion_FirstChunk_OnlyLooksForward()
    {
        // Edge: chunk index 0 must not request index -1.
        var (conn, db, chunks) = await SeedAsync(4);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, [new VectorHit(chunks[0].Id, 0.9)]);
        var filter = new ResolvedSearchFilter(null, null, null, null, WindowSize: 1);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        var item = Assert.Single(results);
        Assert.Equal([1], item.ExpandedChunkIndices);
        Assert.Contains("chunk-1 text", item.Context);
    }

    [Fact]
    public async Task WindowExpansion_FlaggedNeighbour_NeverLeaks()
    {
        // SPEC-20260929 RF-004: a suspicious chunk adjacent to the hit must not
        // re-enter the context via window expansion (ExcludeFlagged applies).
        var (conn, db, chunks) = await SeedAsync(5);
        await using var _c = conn; await using var _d = db;
        chunks[3].SuspicionFlags = "injection";
        await db.SaveChangesAsync();
        var search = NewSearch(db, [new VectorHit(chunks[2].Id, 0.9)]);
        var filter = new ResolvedSearchFilter(null, null, null, null, WindowSize: 1);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        var item = Assert.Single(results);
        Assert.Equal([1], item.ExpandedChunkIndices); // only clean neighbour
        Assert.Contains("chunk-1 text", item.Context);
        Assert.DoesNotContain("chunk-3 text", item.Context!);
    }

    [Fact]
    public async Task WindowExpansion_AdjacentHits_DoNotDuplicateEachOther()
    {
        // SPEC-20260929 RF-007: two neighbouring hits — each context must not
        // embed the other hit's own chunk (already delivered as a result).
        var (conn, db, chunks) = await SeedAsync(5);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db,
            [new VectorHit(chunks[2].Id, 0.95), new VectorHit(chunks[3].Id, 0.9)]);
        var filter = new ResolvedSearchFilter(null, null, null, null, WindowSize: 1);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        var r2 = results.Single(r => r.ChunkId == chunks[2].Id);
        var r3 = results.Single(r => r.ChunkId == chunks[3].Id);
        Assert.DoesNotContain(3, r2.ExpandedChunkIndices!); // hit #3 not duplicated
        Assert.DoesNotContain(2, r3.ExpandedChunkIndices!); // hit #2 not duplicated
    }

    private sealed class StubEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
    }

    private sealed class StubVectorStore(IReadOnlyList<VectorHit> hits) : IVectorStore
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
            Task.FromResult(hits);
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
