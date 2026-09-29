using KnowledgeHub.Server.Search;
using KnowledgeHub.Shared.Contracts;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Search;

// Covers SPEC-20260927-chunk-window-retrieval-and-autocut RF-003 + AC-3:
// single-elbow cut, smooth decline, noisy tail, identical scores, clamps.
public sealed class AutocutFilterTests
{
    private static SearchResultItem Hit(double score) =>
        new()
        {
            ChunkText = "t",
            DocumentTitle = "d",
            SourceName = "s",
            SourceId = Guid.NewGuid(),
            Score = score,
            UriReference = "u"
        };

    [Fact]
    public void SingleElbow_CutsAfterTheDrop()
    {
        // AC-3: [0.96, 0.94, 0.91, 0.40, 0.38, 0.20], sensitivity 1 → keep first 3.
        var items = new[] { 0.96, 0.94, 0.91, 0.40, 0.38, 0.20 }.Select(Hit).ToList();

        var kept = AutocutFilter.Apply(items, sensitivity: 1);

        Assert.Equal(3, kept.Count);
        Assert.Equal([0.96, 0.94, 0.91], kept.Select(i => i.Score));
    }

    [Fact]
    public void IdenticalScores_NoElbow_KeepsAll()
    {
        // Edge case: flat curve → no drop → return everything (up to TopK).
        var items = new[] { 0.8, 0.8, 0.8, 0.8 }.Select(Hit).ToList();

        Assert.Equal(4, AutocutFilter.Apply(items, 1).Count);
    }

    [Fact]
    public void SmoothDecline_NoAbruptDrop_KeepsAll()
    {
        var items = new[] { 1.0, 0.9, 0.8, 0.7, 0.6 }.Select(Hit).ToList();

        Assert.Equal(5, AutocutFilter.Apply(items, 1).Count);
    }

    [Fact]
    public void SensitivityTwo_SkipsFirstElbow()
    {
        // Two elbows (after idx 1 and idx 4); sensitivity 2 cuts at the second.
        var items = new[] { 0.9, 0.85, 0.55, 0.5, 0.45, 0.1 }.Select(Hit).ToList();

        Assert.Equal(2, AutocutFilter.Apply(items, sensitivity: 1).Count);
        Assert.Equal(5, AutocutFilter.Apply(items, sensitivity: 2).Count);
    }

    [Fact]
    public void MaxClamp_CapsTheResult()
    {
        var items = Enumerable.Range(0, 30).Select(i => Hit(1.0 - i * 0.001)).ToList();

        var kept = AutocutFilter.Apply(items, sensitivity: 1, maxClamp: 20);

        Assert.Equal(20, kept.Count);
    }

    [Fact]
    public void EmptyAndSingleton_ReturnUnchanged()
    {
        Assert.Empty(AutocutFilter.Apply([], 1));
        Assert.Single(AutocutFilter.Apply([Hit(0.5)], 1));
    }

    [Fact]
    public void FusedScore_PreferredOverRawScore()
    {
        // RRF-fused scores (~0.016) must drive the cut, not the raw Score field.
        var items = new[]
        {
            Hit(0.9) with { ScoreBreakdown = new SearchScoreBreakdown { Fused = 0.0164 } },
            Hit(0.8) with { ScoreBreakdown = new SearchScoreBreakdown { Fused = 0.0161 } },
            Hit(0.7) with { ScoreBreakdown = new SearchScoreBreakdown { Fused = 0.0040 } },
            Hit(0.6) with { ScoreBreakdown = new SearchScoreBreakdown { Fused = 0.0039 } }
        };

        Assert.Equal(2, AutocutFilter.Apply(items, 1).Count);
    }
    [Fact]
    public void RerankedList_UsesRerankScore_NotFused()
    {
        // SPEC-20260929 RF-002: after reranking, the score curve is Rerank —
        // autocut must read it, not the stale Fused values.
        var fusedHighThenFlat = new[]
        {
            (fused: 0.9, rerank: 0.9), (fused: 0.9, rerank: 0.9), (fused: 0.1, rerank: 0.1)
        };
        var items = fusedHighThenFlat.Select(t => new SearchResultItem
        {
            ChunkText = "t",
            DocumentTitle = "d",
            SourceName = "s",
            SourceId = Guid.NewGuid(),
            Score = t.fused,
            UriReference = "u",
            ScoreBreakdown = new SearchScoreBreakdown { Fused = t.fused, Rerank = t.rerank }
        }).ToList();

        var kept = AutocutFilter.Apply(items, sensitivity: 1);

        Assert.Equal(2, kept.Count); // elbow on the RERANK curve after item 2
        Assert.Equal(0.9, AutocutFilter.EffectiveScore(kept[0]));
    }
}

