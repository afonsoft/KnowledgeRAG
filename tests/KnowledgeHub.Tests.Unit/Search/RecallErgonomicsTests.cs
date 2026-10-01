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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Search;

// Covers SPEC-20261001-mcp-recall-ergonomics RF-001..RF-004: budget levels,
// per-stage score floors, the temporal boost window and the token budget.
[Collection("SearchTelemetry")]
public sealed class RecallErgonomicsTests
{
    // ---------------------------------------------------------------------
    // ResolvedSearchFilter — validation + cache identity
    // ---------------------------------------------------------------------

    private static ResolvedSearchFilter Resolve(SearchFilter raw) =>
        ResolvedSearchFilter.TryResolve(raw, out var filter, out var error)
            ? filter
            : throw new InvalidOperationException(error);

    [Fact]
    public void Budget_Invalid_Fails()
    {
        var ok = ResolvedSearchFilter.TryResolve(
            new SearchFilter { Budget = "bogus" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("budget", error);
    }

    [Theory]
    [InlineData("low", true)]
    [InlineData("mid", true)]
    [InlineData("high", false)]
    public void Budget_Resolves_AndClassifies(string budget, bool expectedSkipsExpansion)
    {
        var filter = Resolve(new SearchFilter { Budget = budget });

        Assert.Equal(budget, filter.Budget);
        Assert.Equal(expectedSkipsExpansion, filter.SkipsExpansion);
    }

    [Theory]
    [InlineData(1.5, null, null)]
    [InlineData(-0.1, null, null)]
    [InlineData(null, 1.5, null)]
    [InlineData(null, null, 2.0)]
    public void MinScores_OutOfRange_Fails(double? semantic, double? lexical, double? final)
    {
        var ok = ResolvedSearchFilter.TryResolve(
            new SearchFilter
            {
                MinScores = new SearchMinScores
                { Semantic = semantic, Lexical = lexical, Final = final }
            }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("minScores", error);
    }

    [Fact]
    public void MinScores_AllNull_NormalizesToNull()
    {
        var filter = Resolve(new SearchFilter { MinScores = new SearchMinScores() });
        Assert.Null(filter.MinScores);
    }

    [Fact]
    public void TemporalWindow_InvalidDate_Fails()
    {
        var ok = ResolvedSearchFilter.TryResolve(
            new SearchFilter { TemporalStart = "not-a-date" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("temporalWindow.start", error);
    }

    [Fact]
    public void TemporalWindow_Reversed_Fails()
    {
        var ok = ResolvedSearchFilter.TryResolve(
            new SearchFilter { TemporalStart = "2026-10-01", TemporalEnd = "2026-01-01" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("start must precede end", error);
    }

    [Fact]
    public void TemporalWindow_Valid_Resolves()
    {
        var filter = Resolve(new SearchFilter
        { TemporalStart = "2026-01-01", TemporalEnd = "2026-09-30T23:59:59Z" });

        Assert.Equal(DateTimeOffset.Parse("2026-01-01"), filter.TemporalStart);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T23:59:59Z"), filter.TemporalEnd);
    }

    [Fact]
    public void Fingerprint_Discriminates_NewFields()
    {
        var baseline = Resolve(new SearchFilter()).Fingerprint();
        var low = Resolve(new SearchFilter { Budget = "low" }).Fingerprint();
        var mid = Resolve(new SearchFilter { Budget = "mid" }).Fingerprint();
        var floors = Resolve(new SearchFilter
        {
            MinScores = new SearchMinScores { Semantic = 0.5, Final = 0.02 }
        }).Fingerprint();
        var window = Resolve(new SearchFilter
        { TemporalStart = "2026-01-01", TemporalEnd = "2026-09-30" }).Fingerprint();

        Assert.Equal(5, new[] { baseline, low, mid, floors, window }.Distinct().Count());
    }

    // ---------------------------------------------------------------------
    // SearchService — floors, boost, budget window, expansion suppression
    // ---------------------------------------------------------------------

    private static async Task<(SqliteConnection conn, KnowledgeHubDbContext db,
            KnowledgeSource source, List<DocumentChunk> chunks)>
        SeedAsync(params (string Title, string Text, DateTimeOffset IndexedAt)[] docs)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.DocumentFile, IsActive = true };
        db.Sources.Add(source);
        var chunks = new List<DocumentChunk>();
        foreach (var (title, text, indexedAt) in docs)
        {
            var doc = new KnowledgeDocument
            {
                Title = title,
                UriReference = $"s/{title}",
                KnowledgeSourceId = source.Id,
                IndexedAt = indexedAt
            };
            var chunk = new DocumentChunk
            {
                KnowledgeDocumentId = doc.Id,
                ChunkIndex = 0,
                TextContent = text
            };
            db.Documents.Add(doc);
            db.Chunks.Add(chunk);
            chunks.Add(chunk);
        }
        await db.SaveChangesAsync();
        return (conn, db, source, chunks);
    }

    private static SearchService NewSearch(KnowledgeHubDbContext db,
        IVectorStore vectors, ILexicalSearchService? lexical = null,
        IConfiguration? config = null, IQueryExpander? expander = null)
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new StubEmbeddings();
        return new SearchService(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb),
            vectors, lexical ?? new DisabledLexical(), cache,
            config ?? new ConfigurationBuilder().Build(), new PassthroughRewriter(),
            expander ?? NoOpExpander.Instance,
            new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            NoOpReranker.Instance, new UnrestrictedScope(), FakeGraphSettings.Disabled,
            NullLogger<SearchService>.Instance);
    }

    [Fact]
    public async Task MinSemantic_FastPath_PrunesWeakHits()
    {
        // RF-003: the semantic floor applies on the plain semantic path too —
        // not only inside the fused expansion arms.
        var (conn, db, _, chunks) = await SeedAsync(("a", "text a", DateTimeOffset.UtcNow),
            ("b", "text b", DateTimeOffset.UtcNow));
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, new FixedVectorStore(
            [new VectorHit(chunks[0].Id, 0.9), new VectorHit(chunks[1].Id, 0.5)]));
        var filter = Resolve(new SearchFilter
        { MinScores = new SearchMinScores { Semantic = 0.7 } });

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        var item = Assert.Single(results);
        Assert.Equal(chunks[0].Id, item.ChunkId);
    }

    [Fact]
    public async Task MinLexical_PrunesByFractionOfBest()
    {
        // RF-003: lexical floor is a fraction of the arm's best BM25 (FTS5 bm25
        // is more-negative-better) — best -10, floor 0.6 keeps only ≤ -6.
        var (conn, db, _, chunks) = await SeedAsync(("a", "text a", DateTimeOffset.UtcNow),
            ("b", "text b", DateTimeOffset.UtcNow));
        await using var _c = conn; await using var _d = db;
        var lexical = new ScriptedLexical(
            [new LexicalHit(chunks[0].Id, 1, -10), new LexicalHit(chunks[1].Id, 2, -4)]);
        var search = NewSearch(db, new FixedVectorStore([]), lexical);
        var filter = Resolve(new SearchFilter
        { MinScores = new SearchMinScores { Lexical = 0.6 } });

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Lexical, filter: filter);

        var item = Assert.Single(results);
        Assert.Equal(chunks[0].Id, item.ChunkId);
    }

    [Fact]
    public async Task MinFinal_EmptiesResult_WhenNothingPasses()
    {
        // RF-003: post-fusion floor — empty result is the honest answer.
        var (conn, db, _, chunks) = await SeedAsync(("a", "text a", DateTimeOffset.UtcNow));
        await using var _c = conn; await using var _d = db;
        var search = NewSearch(db, new FixedVectorStore([new VectorHit(chunks[0].Id, 0.9)]));
        var filter = Resolve(new SearchFilter
        { MinScores = new SearchMinScores { Final = 0.95 } });

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        Assert.Empty(results);
    }

    [Fact]
    public async Task TemporalBoost_InWindowHit_RanksFirst()
    {
        // RF-004: in-window documents are boosted (×1.1), not filtered — the
        // out-of-window hit stays in the list.
        var old = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var (conn, db, _, chunks) = await SeedAsync(
            ("old", "text old", old), ("new", "text new", recent));
        await using var _c = conn; await using var _d = db;
        // Equal scores — without the boost the original order would be kept.
        var search = NewSearch(db, new FixedVectorStore(
            [new VectorHit(chunks[0].Id, 0.9), new VectorHit(chunks[1].Id, 0.9)]));
        var filter = Resolve(new SearchFilter
        { TemporalStart = "2026-01-01", TemporalEnd = "2026-12-31" });

        var results = await search.SearchAsync("q", 5, mode: SearchMode.Semantic, filter: filter);

        Assert.Equal(2, results.Count);
        Assert.Equal(chunks[1].Id, results[0].ChunkId);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task Budget_Low_HalvesArmWindow()
    {
        // RF-001: low = half the candidate pool (topK×2 vs the usual topK×4).
        var (conn, db, _, chunks) = await SeedAsync(("a", "text a", DateTimeOffset.UtcNow));
        await using var _c = conn; await using var _d = db;
        var store = new RecordingVectorStore([new VectorHit(chunks[0].Id, 0.9)]);
        var search = NewSearch(db, store);

        await search.SearchAsync("q", 10, mode: SearchMode.Hybrid);
        var lowFilter = Resolve(new SearchFilter { Budget = "low" });
        await search.SearchAsync("q", 10, mode: SearchMode.Hybrid, filter: lowFilter);
        var midFilter = Resolve(new SearchFilter { Budget = "mid" });
        await search.SearchAsync("q", 10, mode: SearchMode.Hybrid, filter: midFilter);

        Assert.Equal([40, 20, 40], store.RequestedTopKs);
    }

    [Fact]
    public async Task Budget_Mid_SkipsExpansion_ButHighExpands()
    {
        // RF-001: mid keeps the default pool but never pays for query expansion.
        var (conn, db, _, chunks) = await SeedAsync(("a", "text a", DateTimeOffset.UtcNow));
        await using var _c = conn; await using var _d = db;
        var expander = new CountingExpander();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Search:QueryExpansion:Mode"] = "multi" })
            .Build();
        var search = NewSearch(db,
            new FixedVectorStore([new VectorHit(chunks[0].Id, 0.9)]),
            config: config, expander: expander);

        var mid = Resolve(new SearchFilter { Budget = "mid" });
        await search.SearchAsync("q", 5, mode: SearchMode.Hybrid, filter: mid);
        Assert.Equal(0, expander.Calls);

        var high = Resolve(new SearchFilter { Budget = "high" });
        await search.SearchAsync("q", 5, mode: SearchMode.Hybrid, filter: high);
        Assert.True(expander.Calls > 0);
    }

    // ---------------------------------------------------------------------
    // CorrectiveRetrievalService — low budget skips retry; final floor abstains
    // ---------------------------------------------------------------------

    private static SearchResultItem Hit(double fused) => new()
    {
        ChunkText = "text",
        DocumentTitle = "doc",
        SourceName = "src",
        SourceId = Guid.NewGuid(),
        Score = fused,
        UriReference = "uri",
        ScoreBreakdown = new SearchScoreBreakdown { Fused = fused }
    };

    private static IConfiguration Cfg(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();

    private static CorrectiveRetrievalService Sut(
        PagedSearchService search, IConfiguration cfg, IQueryRewriter? rewriter = null,
        IRetrievalGrader? grader = null) =>
        new(search,
            grader ?? new HeuristicRetrievalGrader(cfg),
            rewriter ?? new StaticRewriter("better query"),
            cfg,
            NullLogger<CorrectiveRetrievalService>.Instance);

    [Fact]
    public async Task Budget_Low_SkipsGradingRetry()
    {
        // RF-001: a weak grade under budget=low is reported, never retried.
        var search = new PagedSearchService([[Hit(0.015)], [Hit(0.03), Hit(0.03)]]);
        var cfg = Cfg(new() { ["Search:Grading:Mode"] = "heuristic" });
        var sut = Sut(search, cfg);
        var low = Resolve(new SearchFilter { Budget = "low" });

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, low);

        Assert.Equal(RetrievalGrade.Weak, outcome.Grading.Grade); // still graded…
        Assert.False(outcome.Retried);                            // …but not retried
        Assert.Equal(1, search.Calls);
    }

    [Fact]
    public async Task Budget_Mid_AllowsGradingRetry()
    {
        var search = new PagedSearchService([[Hit(0.015)], [Hit(0.03), Hit(0.03)]]);
        var cfg = Cfg(new() { ["Search:Grading:Mode"] = "heuristic" });
        var sut = Sut(search, cfg);
        var mid = Resolve(new SearchFilter { Budget = "mid" });

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, mid);

        Assert.True(outcome.Retried);
        Assert.Equal(2, search.Calls);
    }

    [Fact]
    public async Task FinalFloor_Empty_GradingOff_StillInsufficient()
    {
        // RF-003: an explicit final floor that empties results is an abstention
        // request even with grading off — never synthesize without evidence.
        var search = new PagedSearchService([[]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "off" }));
        var floored = Resolve(new SearchFilter
        { MinScores = new SearchMinScores { Final = 0.5 } });

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, floored);

        Assert.Equal(RetrievalGrade.Insufficient, outcome.Grading.Grade);
        Assert.Equal(1, search.Calls);
    }

    [Fact]
    public async Task FinalFloor_Empty_GradingOn_ForcesInsufficient()
    {
        // Defensive: even a fail-open grader's Sufficient is overridden when a
        // caller-set final floor emptied the result set.
        var search = new PagedSearchService([[]]);
        var cfg = Cfg(new() { ["Search:Grading:Mode"] = "heuristic" });
        var sut = Sut(search, cfg, grader: new FixedGrader(RetrievalGrade.Sufficient));
        var floored = Resolve(new SearchFilter
        { MinScores = new SearchMinScores { Final = 0.5 } });

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, floored);

        Assert.Equal(RetrievalGrade.Insufficient, outcome.Grading.Grade);
    }

    [Fact]
    public async Task NoFloor_Empty_GradingOff_StaysSufficient()
    {
        // Without a floor an empty result stays a fail-open Sufficient —
        // pre-feature behavior is unchanged.
        var search = new PagedSearchService([[]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "off" }));

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, null);

        Assert.Equal(RetrievalGrade.Sufficient, outcome.Grading.Grade);
    }

    // ---------------------------------------------------------------------
    // maxTokens — ApplyTokenBudget
    // ---------------------------------------------------------------------

    private static SearchResultItem SizedHit(int chars, int contextChars = 0) => new()
    {
        ChunkText = new string('x', chars),
        DocumentTitle = "doc",
        SourceName = "src",
        SourceId = Guid.NewGuid(),
        Score = 0.9,
        UriReference = "uri",
        Context = contextChars > 0 ? new string('c', contextChars) : null
    };

    [Fact]
    public void TokenBudget_UnderBudget_NothingCut()
    {
        var (items, truncated) = KnowledgeToolsProvider.ApplyTokenBudget(
            [SizedHit(400), SizedHit(400)], 4096); // 800 chars < 16k

        Assert.Equal(2, items.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void TokenBudget_ExactFit_NoTruncation()
    {
        var (items, truncated) = KnowledgeToolsProvider.ApplyTokenBudget(
            [SizedHit(600), SizedHit(424)], 256); // 1024 == 256*4

        Assert.Equal(2, items.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void TokenBudget_OverBudget_DropsTail()
    {
        var (items, truncated) = KnowledgeToolsProvider.ApplyTokenBudget(
            [SizedHit(900), SizedHit(200), SizedHit(10)], 256); // 1024 char cap

        Assert.Single(items);
        Assert.True(truncated);
    }

    [Fact]
    public void TokenBudget_CountsExpandedContext()
    {
        var (items, truncated) = KnowledgeToolsProvider.ApplyTokenBudget(
            [SizedHit(600, contextChars: 500)], 256); // 1100 > 1024 but…

        Assert.Single(items); // …the lone hit is always kept
        Assert.False(truncated); // nothing was dropped → not "truncated"
    }

    [Fact]
    public void TokenBudget_LoneOverBudgetHit_Kept()
    {
        var (items, truncated) = KnowledgeToolsProvider.ApplyTokenBudget(
            [SizedHit(10_000)], 256);

        var item = Assert.Single(items);
        Assert.Equal(10_000, item.ChunkText.Length);
        Assert.False(truncated);
    }

    // ---------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------

    private sealed class StubEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
    }

    private sealed class FixedVectorStore(IReadOnlyList<VectorHit> hits) : IVectorStore
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
            Task.FromResult<IReadOnlyList<VectorHit>>(hits.Take(topK).ToList());
    }

    /// <summary>Records the per-arm candidate window each call was asked for.</summary>
    private sealed class RecordingVectorStore(IReadOnlyList<VectorHit> hits) : IVectorStore
    {
        public List<int> RequestedTopKs { get; } = [];

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
            RequestedTopKs.Add(topK);
            return Task.FromResult<IReadOnlyList<VectorHit>>(hits.Take(topK).ToList());
        }
    }

    private sealed class ScriptedLexical(IReadOnlyList<LexicalHit> hits) : ILexicalSearchService
    {
        public bool Enabled => true;
        public Task<IReadOnlyList<LexicalHit>> SearchAsync(string query, int topK,
            IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(hits);
        public Task ReconcileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class CountingExpander : IQueryExpander
    {
        public int Calls;
        public Task<IReadOnlyList<string>> ExpandQueriesAsync(
            string query, int count, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(["variant"]);
        }
        public Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class UnrestrictedScope : KnowledgeHub.Server.Auth.ICallerScopeProvider
    {
        public Task<KnowledgeHub.Server.Auth.CallerScope> GetAsync(CancellationToken ct) =>
            Task.FromResult(KnowledgeHub.Server.Auth.CallerScope.Unrestricted);
    }

    private sealed class PagedSearchService(List<IReadOnlyList<SearchResultItem>> pages) : ISearchService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            string query, int topK, Guid? sourceId = null,
            SearchMode mode = SearchMode.Hybrid, ResolvedSearchFilter? filter = null,
            string? conversationContext = null, CancellationToken ct = default)
        {
            var page = pages[Math.Min(Calls, pages.Count - 1)];
            Calls++;
            return Task.FromResult(page);
        }
    }

    private sealed class StaticRewriter(string output) : IQueryRewriter
    {
        public Task<string> RewriteAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FixedGrader(RetrievalGrade grade) : IRetrievalGrader
    {
        public Task<RetrievalGrading> GradeAsync(
            string query, IReadOnlyList<SearchResultItem> results, CancellationToken ct = default) =>
            Task.FromResult(new RetrievalGrading(grade, 0));
    }
}
