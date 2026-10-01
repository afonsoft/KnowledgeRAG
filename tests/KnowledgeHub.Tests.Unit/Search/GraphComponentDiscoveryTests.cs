using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Mcp.ToolProviders;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Unit.Search;

/// <summary>
/// SPEC-20260924-graph-tool-discovery: chunk→component enrichment on the
/// search path, disabled gate, citation propagation, and text surfaces.
/// </summary>
[Collection("SearchTelemetry")]
public class GraphComponentDiscoveryTests
{
    private static async Task<(SqliteConnection conn, KnowledgeHubDbContext db, DocumentChunk chunk)>
        SeedAsync(bool withEdge = true)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        var chunk = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 0, TextContent = "text" };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);

        if (withEdge)
        {
            var a = new KgNode { Name = "OmniRoute", NormalizedName = "omniroute", Type = "service" };
            var b = new KgNode { Name = "AgentRouter", NormalizedName = "agentrouter", Type = "service" };
            db.KgNodes.AddRange(a, b);
            db.KgEdges.Add(new KgEdge
            {
                From = a,
                To = b,
                Kind = "DEPENDS_ON",
                EvidenceChunkId = chunk.Id,
                KnowledgeDocumentId = doc.Id,
                KnowledgeSourceId = source.Id
            });
        }
        await db.SaveChangesAsync();
        return (conn, db, chunk);
    }

    private static SearchService NewSearch(KnowledgeHubDbContext db, DocumentChunk chunk,
        FakeGraphSettings graph)
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new StubEmbeddings();
        return new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb), new StubVectorStore(new VectorHit(chunk.Id, 0.9)), new DisabledLexical(), cache), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), graph), new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);
    }

    [Fact]
    public async Task Search_AttachesComponents_FromEdgeEvidence()
    {
        var (conn, db, chunk) = await SeedAsync();
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, chunk, FakeGraphSettings.Enabled);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic);

        var item = Assert.Single(results);
        Assert.Equal(["AgentRouter", "OmniRoute"], item.Components);
    }

    [Fact]
    public async Task Search_GraphDisabled_LeavesComponentsNull()
    {
        var (conn, db, chunk) = await SeedAsync();
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, chunk, FakeGraphSettings.Disabled);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic);

        Assert.Null(Assert.Single(results).Components);
    }

    [Fact]
    public async Task Search_NoEdges_LeavesComponentsNull()
    {
        var (conn, db, chunk) = await SeedAsync(withEdge: false);
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, chunk, FakeGraphSettings.Enabled);

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic);

        Assert.Null(Assert.Single(results).Components);
    }

    [Fact]
    public void ExtractCitations_PropagatesComponents()
    {
        var items = new List<SearchResultItem> { Item() with { Components = ["OmniRoute"] } };

        var citations = AnswerService.ExtractCitations("answer [1]", items);

        Assert.Equal(["OmniRoute"], Assert.Single(citations).Components);
    }

    [Fact]
    public void FormatHits_SurfacesComponents()
    {
        var text = KnowledgeToolsProvider.FormatHits([Item() with { Components = ["OmniRoute", "AgentRouter"] }]);

        Assert.Contains("components: OmniRoute, AgentRouter", text);
    }

    private static SearchResultItem Item() => new()
    {
        ChunkText = "text",
        DocumentTitle = "d",
        SourceName = "s",
        SourceId = Guid.NewGuid(),
        SourceType = SourceType.WebPage,
        Score = 0.9,
        UriReference = "uri-1",
        ChunkId = Guid.NewGuid()
    };

    private sealed class StubEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
    }

    private sealed class StubVectorStore(VectorHit hit) : IVectorStore
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
            Task.FromResult<IReadOnlyList<VectorHit>>([hit]);
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
