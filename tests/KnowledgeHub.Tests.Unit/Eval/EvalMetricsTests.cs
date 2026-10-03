using KnowledgeHub.Server.Eval;
using KnowledgeHub.Shared.Contracts;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Eval;

/// <summary>
/// SPEC-20260923-eval-harness RF-002 — hand-computed metric fixtures.
/// </summary>
public sealed class EvalMetricsTests
{
    private static SearchResultItem Hit(string uri, string text = "text") => new()
    {
        ChunkText = text,
        DocumentTitle = "t",
        SourceName = "s",
        SourceId = Guid.NewGuid(),
        Score = 0.9,
        UriReference = uri
    };

    private static EvalCase Case(
        string[]? uris = null, string[]? markers = null, bool noAnswer = false) => new()
        {
            Id = "c1",
            Question = "q",
            ExpectedUris = uris ?? [],
            ExpectedTextMarkers = markers ?? [],
            ExpectNoAnswer = noAnswer
        };

    [Fact]
    public void IsHit_ByUri()
    {
        Assert.True(EvalMetrics.IsHit(Hit("file://a.md"), Case(uris: ["file://a.md"])));
        Assert.False(EvalMetrics.IsHit(Hit("file://b.md"), Case(uris: ["file://a.md"])));
    }

    [Fact]
    public void IsHit_ByAllMarkers_CaseInsensitive()
    {
        Assert.True(EvalMetrics.IsHit(Hit("u", "the Cat SAT"), Case(markers: ["cat", "sat"])));
        Assert.False(EvalMetrics.IsHit(Hit("u", "the cat"), Case(markers: ["cat", "sat"])));
    }

    [Fact]
    public void RecallAtK_PartialCoverage()
    {
        var results = new[] { Hit("a"), Hit("x"), Hit("b") };
        // expected {a,b,c}: found a,b → 2/3
        Assert.Equal(0.6667, EvalMetrics.Round(EvalMetrics.RecallAtK(results, Case(uris: ["a", "b", "c"]))));
    }

    [Fact]
    public void RecallAtK_DedupesExpected()
    {
        var results = new[] { Hit("a") };
        Assert.Equal(1.0, EvalMetrics.RecallAtK(results, Case(uris: ["a", "a"])));
    }

    [Fact]
    public void PrecisionAtK_CountsHits()
    {
        var results = new[] { Hit("a"), Hit("x"), Hit("b"), Hit("y") };
        // hits a,b of 4 returned → 0.5
        Assert.Equal(0.5, EvalMetrics.PrecisionAtK(results, Case(uris: ["a", "b"])));
    }

    [Fact]
    public void ReciprocalRank_FirstHitRank()
    {
        var results = new[] { Hit("x"), Hit("y"), Hit("a") };
        Assert.Equal(1.0 / 3, EvalMetrics.ReciprocalRank(results, Case(uris: ["a"])));
        Assert.Equal(0.0, EvalMetrics.ReciprocalRank(results, Case(uris: ["z"])));
    }

    [Fact]
    public void NdcgAtK_HitsFirstScoresOne()
    {
        // hits at ranks 1-2 → DCG == IDCG → 1 regardless of trailing misses
        var results = new[] { Hit("a"), Hit("b"), Hit("x"), Hit("y") };
        Assert.Equal(1.0, EvalMetrics.NdcgAtK(results, Case(uris: ["a", "b"])));
    }

    [Fact]
    public void NdcgAtK_LateHitScoresBelowOne()
    {
        // one hit at rank 3 of 3 → DCG=1/log2(4)=0.5, IDCG=1 → 0.5
        var results = new[] { Hit("x"), Hit("y"), Hit("a") };
        Assert.Equal(0.5, EvalMetrics.NdcgAtK(results, Case(uris: ["a"])));
    }

    [Fact]
    public void NdcgAtK_NoHitsIsZero_ExpectNoAnswerMirrorsRecall()
    {
        Assert.Equal(0.0, EvalMetrics.NdcgAtK([Hit("x")], Case(uris: ["a"])));
        Assert.Equal(1.0, EvalMetrics.NdcgAtK([], Case(noAnswer: true)));
        Assert.Equal(0.0, EvalMetrics.NdcgAtK([Hit("a")], Case(noAnswer: true)));
    }

    [Fact]
    public void ExpectNoAnswer_EmptyScoresOne_NonEmptyZeroAndSkipsRr()
    {
        var empty = Array.Empty<SearchResultItem>();
        var noAnswer = Case(noAnswer: true);
        Assert.Equal(1.0, EvalMetrics.RecallAtK(empty, noAnswer));
        Assert.Equal(1.0, EvalMetrics.PrecisionAtK(empty, noAnswer));
        Assert.Equal(0.0, EvalMetrics.ReciprocalRank(empty, noAnswer));

        var hits = new[] { Hit("a") };
        Assert.Equal(0.0, EvalMetrics.RecallAtK(hits, noAnswer));
        Assert.Equal(0.0, EvalMetrics.PrecisionAtK(hits, noAnswer));
    }
}
