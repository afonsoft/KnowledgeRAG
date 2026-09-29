using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// Unit tests for SPEC-20260924-corrective-rag: heuristic grading, corrective
/// retry, and structural abstention.
/// </summary>
[Collection("SearchTelemetry")]
public sealed class CorrectiveRetrievalTests
{
    // -------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeSearchService(List<IReadOnlyList<SearchResultItem>> pages) : ISearchService
    {
        public int Calls { get; private set; }
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            string query, int topK, Guid? sourceId = null,
            SearchMode mode = SearchMode.Hybrid, ResolvedSearchFilter? filter = null,
            string? conversationContext = null, CancellationToken ct = default)
        {
            Queries.Add(query);
            var page = pages[Math.Min(Calls, pages.Count - 1)];
            Calls++;
            return Task.FromResult(page);
        }
    }

    private sealed class FakeRewriter(string output) : IQueryRewriter
    {
        public Task<string> RewriteAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

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
        FakeSearchService search, IConfiguration? cfg = null, IQueryRewriter? rewriter = null) =>
        new(search,
            new HeuristicRetrievalGrader(cfg ?? Cfg()),
            rewriter ?? new FakeRewriter("rewritten query"),
            cfg ?? Cfg(),
            NullLogger<CorrectiveRetrievalService>.Instance);

    // -------------------------------------------------------------------------
    // Heuristic grader
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Grade_EmptyResults_Insufficient()
    {
        var grader = new HeuristicRetrievalGrader(Cfg());
        var g = await grader.GradeAsync("q", []);
        Assert.Equal(RetrievalGrade.Insufficient, g.Grade);
    }

    [Fact]
    public async Task Grade_HighFused_Sufficient()
    {
        var grader = new HeuristicRetrievalGrader(Cfg());
        var g = await grader.GradeAsync("q", [Hit(0.03)]);
        Assert.Equal(RetrievalGrade.Sufficient, g.Grade);
    }

    [Fact]
    public async Task Grade_MidRange_Weak()
    {
        var grader = new HeuristicRetrievalGrader(Cfg());
        var g = await grader.GradeAsync("q", [Hit(0.015)]); // between 0.010 and 0.020
        Assert.Equal(RetrievalGrade.Weak, g.Grade);
    }

    [Fact]
    public async Task Grade_LowScore_Insufficient()
    {
        var grader = new HeuristicRetrievalGrader(Cfg());
        var g = await grader.GradeAsync("q", [Hit(0.005)]);
        Assert.Equal(RetrievalGrade.Insufficient, g.Grade);
    }

    // -------------------------------------------------------------------------
    // Corrective loop
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Retrieve_WeakThenSufficient_RetriesOnceAndKeepsBetter()
    {
        // Given: first page weak, retry page sufficient
        var search = new FakeSearchService([[Hit(0.015)], [Hit(0.03), Hit(0.028)]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "heuristic" }),
            new FakeRewriter("better query"));

        // When
        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, null, ct: CancellationToken.None);

        // Then
        Assert.True(outcome.Retried);
        Assert.Equal(2, search.Calls);
        Assert.Equal("better query", search.Queries[1]);
        Assert.Equal(RetrievalGrade.Sufficient, outcome.Grading.Grade);
        Assert.Equal(2, outcome.Results.Count);
    }

    [Fact]
    public async Task Retrieve_Insufficient_SkipsRetry()
    {
        var search = new FakeSearchService([[]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "heuristic" }));

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, null, ct: CancellationToken.None);

        Assert.Equal(RetrievalGrade.Insufficient, outcome.Grading.Grade);
        Assert.False(outcome.Retried);
        Assert.Equal(1, search.Calls);
    }

    [Fact]
    public async Task Retrieve_RewriteIdentical_StopsWithoutExtraCall()
    {
        var search = new FakeSearchService([[Hit(0.015)]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "heuristic" }),
            rewriter: new FakeRewriter("q")); // same as input

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, null, ct: CancellationToken.None);

        Assert.False(outcome.Retried);
        Assert.Equal(1, search.Calls);
    }

    [Fact]
    public async Task Retrieve_GradingOff_NeverGrades()
    {
        var search = new FakeSearchService([[]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "off" }));

        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, null, ct: CancellationToken.None);

        Assert.Equal(RetrievalGrade.Sufficient, outcome.Grading.Grade); // fail-open
        Assert.Equal(1, search.Calls);
    }

    // -------------------------------------------------------------------------
    // Abstention
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BuildAbstention_Insufficient_MarksResponseAndAttachesTop3()
    {
        var search = new FakeSearchService([[Hit(0.005), Hit(0.004), Hit(0.003), Hit(0.002)]]);
        var sut = Sut(search, Cfg(new() { ["Search:Grading:Mode"] = "heuristic" }));
        var outcome = await sut.RetrieveAsync("q", 5, null, SearchMode.Hybrid, null, ct: CancellationToken.None);

        var response = sut.BuildAbstention("q", outcome);

        Assert.True(response.InsufficientEvidence);
        Assert.False(response.Generated);
        Assert.Equal("insufficient", response.RetrievalGrade);
        Assert.Equal(3, response.Citations.Count);
        Assert.Contains("sufficient evidence", response.Answer);
    }
}
