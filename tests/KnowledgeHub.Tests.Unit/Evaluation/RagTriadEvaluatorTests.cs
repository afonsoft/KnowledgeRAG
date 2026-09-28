using KnowledgeHub.Server.Evaluation;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Evaluation;

// Covers SPEC-20260927-rag-evaluation-triad-metrics RF-002 + AC-1/AC-2.
public class RagTriadEvaluatorTests
{
    private static readonly IRagTriadEvaluator Eval = new RagTriadEvaluator();

    [Fact]
    public void GroundedAnswer_FullySupported_ScoresHigh_NotFlagged()
    {
        var chunks = new[] { "The admin default password is 'changeme' and expires after 90 days." };
        var answer = "The default password is 'changeme' and it expires after 90 days.";

        var r = Eval.Evaluate("What is the default admin password?", chunks, answer);

        Assert.InRange(r.Groundedness, 0.90, 1.0);
        Assert.False(r.FlaggedAsHallucination);
        Assert.InRange(r.ContextRelevance, 0.0, 1.0);
        Assert.InRange(r.AnswerRelevance, 0.0, 1.0);
        Assert.InRange(r.OverallScore, 0.0, 1.0);
    }

    [Fact]
    public void HallucinatedAnswer_InventedNumbers_ScoresLow_Flagged()
    {
        var chunks = new[] { "The server has 4 cores and was deployed in 2024." };
        var answer = "The server has 128 cores and was deployed in 1999.";

        var r = Eval.Evaluate("How many cores does the server have?", chunks, answer);

        Assert.True(r.Groundedness < 0.60);
        Assert.True(r.FlaggedAsHallucination);
    }

    [Fact]
    public void EmptyContext_ZeroContextRelevance()
    {
        var r = Eval.Evaluate("Anything?", [], "No context was provided for this answer.");

        Assert.Equal(0.0, r.ContextRelevance);
    }

    [Fact]
    public void OverallScore_ZeroComponent_IsZero()
    {
        var r = Eval.Evaluate("q?", [], "answer with no context at all");

        Assert.Equal(0.0, r.OverallScore);
    }

    [Fact]
    public void Scores_AreClampedToUnitRange()
    {
        var r = Eval.Evaluate("q", new[] { "q" }, "q");

        Assert.All(new[] { r.ContextRelevance, r.Groundedness, r.AnswerRelevance, r.OverallScore },
            v => Assert.InRange(v, 0.0, 1.0));
    }
}
