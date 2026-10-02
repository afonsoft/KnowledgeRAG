using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Eval;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260926-search-correctness-and-stream: concurrent arms on the
// shared scoped connection (RF-001), retry grading pairing (RF-003) and
// baseline dataset-hash verification (RF-004).
[Collection("SearchTelemetry")]
public sealed class SearchCorrectnessTests
{
    private static (SqliteConnection conn, KnowledgeHubDbContext db) NewDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    private static async Task SeedFtsAsync(KnowledgeHubDbContext db)
    {
        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault };
        db.Sources.Add(source);
        var doc = new KnowledgeDocument
        {
            Title = "d",
            UriReference = "u",
            KnowledgeSourceId = source.Id
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new DocumentChunk
        {
            KnowledgeDocumentId = doc.Id,
            ChunkIndex = 0,
            TextContent = "persistent text"
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE VIRTUAL TABLE chunks_fts USING fts5(
                chunk_id UNINDEXED, text, tokenize='unicode61 remove_diacritics 2');
            INSERT INTO chunks_fts (chunk_id, text) SELECT Id, TextContent FROM Chunks;
            """);
    }

    // ---- RF-001: concurrent lexical calls on the shared :memory: connection
    // must serialize (gate), not race the single SqliteConnection.

    [Fact]
    public async Task Lexical_ConcurrentCalls_OnSharedConnection_AllReturnHits()
    {
        var (conn, db) = NewDb();
        await using var _c = conn; await using var _d = db;
        await SeedFtsAsync(db);
        var lexical = new LexicalSearchService(db,
            new ConfigurationBuilder().Build(),
            new CatalogDatabase(CatalogProvider.Sqlite, null),
            NullLogger<LexicalSearchService>.Instance);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => lexical.SearchAsync("persistent", 5, null)));

        // Without the gate, concurrent readers fault the shared connection and
        // the catch returns [] — non-empty hits prove serialized success.
        Assert.All(results, hits => Assert.NotEmpty(hits));
    }

    [Fact]
    public async Task VecStore_ConcurrentCalls_OnSharedConnection_AllReturnHits()
    {
        var (conn, db) = NewDb();
        await using var _c = conn; await using var _d = db;
        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault };
        var doc = new KnowledgeDocument
        {
            Title = "d",
            UriReference = "u",
            KnowledgeSourceId = source.Id
        };
        var chunk = new DocumentChunk
        {
            KnowledgeDocumentId = doc.Id,
            ChunkIndex = 0,
            TextContent = "t"
        };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var store = new KnowledgeHub.Server.VectorStore.SqliteVecVectorStore(db, dimensions: 4);
        await store.UpsertBatchAsync(
            [new KnowledgeHub.Server.VectorStore.VectorUpsert(chunk.Id, doc.Id, source.Id, new[] { 1f, 0f, 0f, 0f })],
            "m");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.SearchAsync(new[] { 1f, 0f, 0f, 0f }, "m", 3)));

        Assert.All(results, hits => Assert.NotEmpty(hits));
    }

    // ---- RF-003: a worse retry must not replace the kept results' grading.

    private sealed class ScriptedSearch(IReadOnlyList<SearchResultItem> first) : ISearchService
    {
        public int Calls;
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            string query, int topK, Guid? sourceId = null,
            SearchMode mode = SearchMode.Hybrid, ResolvedSearchFilter? filter = null,
            string? conversationContext = null, CancellationToken ct = default)
        {
            Calls++;
            // First call returns hits; the rewritten retry returns nothing.
            return Task.FromResult(Calls == 1 ? first : []);
        }
    }

    private sealed class ScriptedGrader : IRetrievalGrader
    {
        public Task<RetrievalGrading> GradeAsync(
            string query, IReadOnlyList<SearchResultItem> results, CancellationToken ct = default) =>
            Task.FromResult(new RetrievalGrading(
                results.Count > 0 ? RetrievalGrade.Weak : RetrievalGrade.Insufficient, 0.5));
    }

    private sealed class RewriteTo(string rewritten) : IQueryRewriter
    {
        public Task<string> RewriteAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(rewritten);
    }

    [Fact]
    public async Task Retrieve_WorseRetry_KeepsResultsAndTheirGrading()
    {
        var kept = new List<SearchResultItem>
        {
            new() { ChunkText = "t", DocumentTitle = "d", SourceName = "s",
                    SourceId = Guid.NewGuid(), Score = 0.1, UriReference = "u" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Search:Grading:Mode"] = "heuristic",
            ["Search:Grading:MaxRetries"] = "1"
        }).Build();
        var service = new CorrectiveRetrievalService(
            new ScriptedSearch(kept), new ScriptedGrader(), new RewriteTo("q2"),
            config, NullLogger<CorrectiveRetrievalService>.Instance);

        var outcome = await service.RetrieveAsync("q1", 5, null, SearchMode.Hybrid, null);

        // The retry graded Insufficient on zero hits — the kept attempt stays
        // Weak with its results (previously grading flipped to the worse
        // attempt's verdict, causing an unjustified abstention).
        Assert.Single(outcome.Results);
        Assert.Equal(RetrievalGrade.Weak, outcome.Grading.Grade);
        Assert.True(outcome.Retried);
    }

    // ---- RF-004: a baseline pinned to a different dataset must not compare.

    [Fact]
    public async Task Eval_BaselineOnDifferentDataset_Throws()
    {
        var (conn, db) = NewDb();
        await using var _c = conn; await using var _d = db;
        var run = new EvalRun { DatasetHash = "DEADBEEF" };
        db.EvalRuns.Add(run);
        db.EvalBaselines.Add(new EvalBaseline
        {
            Name = "golden",
            EvalRunId = run.Id,
            DatasetHash = "DEADBEEF"
        });
        await db.SaveChangesAsync();

        var runner = new EvalRunner(new ScriptedSearch([]),
            new ServiceCollection().BuildServiceProvider(), db,
            NullLogger<EvalRunner>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync([new EvalCase { Id = "c1", Question = "q" }], "[{\"id\":\"c1\"}]", new EvalRunner.EvalRunOptions(BaselineName: "golden")));
        Assert.Contains("different dataset", ex.Message);
    }
}
