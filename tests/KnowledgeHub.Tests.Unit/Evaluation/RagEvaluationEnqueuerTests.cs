using KnowledgeHub.Server.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Evaluation;

// Covers SPEC-20260927-rag-evaluation-triad-metrics RF-001 + AC-3.
public class RagEvaluationEnqueuerTests
{
    [Fact]
    public void SampleRateZero_EnqueuesNothing()
    {
        var enq = new RagEvaluationEnqueuer(
            Options.Create(new RagEvaluationOptions { Enabled = true, SampleRate = 0.0 }));

        for (var i = 0; i < 200; i++)
            Assert.False(enq.TryEnqueue("q", "q", ["c"], "a"));
        Assert.False(enq.Reader.TryRead(out _));
    }

    [Fact]
    public void Disabled_EnqueuesNothing()
    {
        var enq = new RagEvaluationEnqueuer(
            Options.Create(new RagEvaluationOptions { Enabled = false, SampleRate = 1.0 }));

        Assert.False(enq.TryEnqueue("q", "q", ["c"], "a"));
    }

    [Fact]
    public void SampleRateOne_EnqueuesEveryTime()
    {
        var enq = new RagEvaluationEnqueuer(
            Options.Create(new RagEvaluationOptions { Enabled = true, SampleRate = 1.0 }));

        for (var i = 0; i < 50; i++)
            Assert.True(enq.TryEnqueue("q", "q", ["c"], "a"));

        var count = 0;
        while (enq.Reader.TryRead(out _)) count++;
        Assert.Equal(50, count);
    }

    [Fact]
    public void PartialSampleRate_DeterministicStratified()
    {
        // PR #367 follow-up: stratified sampling admits exactly the configured
        // fraction of each 100-ticket window — no RNG.
        var enq = new RagEvaluationEnqueuer(
            Options.Create(new RagEvaluationOptions { Enabled = true, SampleRate = 0.2 }));

        var admitted = 0;
        for (var i = 0; i < 1000; i++)
            if (enq.TryEnqueue("q", "q", ["c"], "a"))
                admitted++;

        Assert.Equal(200, admitted);
    }
}
