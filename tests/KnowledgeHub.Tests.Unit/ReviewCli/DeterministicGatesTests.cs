using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Gate;
using KnowledgeHub.Review.Signals;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class DeterministicGatesTests
{
    private static readonly ReviewOptions Options = new();

    [Fact]
    public void Clean_pr_proceeds()
    {
        var result = DeterministicGates.Evaluate(TestSignals.Signal(), Options);
        Assert.Equal("proceed", result.Outcome);
        Assert.True(result.Proceeds);
    }

    [Fact]
    public void Draft_pr_skips()
    {
        var signal = TestSignals.Signal(meta: TestSignals.Meta(isDraft: true));
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("skip", result.Outcome);
        Assert.Contains(result.Reasons, r => r.Contains("draft"));
    }

    [Fact]
    public void Skip_label_skips_case_insensitive()
    {
        var signal = TestSignals.Signal(meta: TestSignals.Meta(labels: ["Knowledge-Review:SKIP"]));
        Assert.Equal("skip", DeterministicGates.Evaluate(signal, Options).Outcome);
    }

    [Fact]
    public void Human_changes_requested_blocks()
    {
        var signal = TestSignals.Signal(human: [new HumanReviewSignal("reviewer", "CHANGES_REQUESTED", DateTimeOffset.UtcNow)]);
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("block", result.Outcome);
        Assert.Contains(result.Reasons, r => r.Contains("CHANGES_REQUESTED"));
    }

    [Fact]
    public void Failing_required_check_blocks()
    {
        var signal = TestSignals.Signal(checks: [TestSignals.Check("Unit Tests", "completed", "failure")]);
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("block", result.Outcome);
    }

    [Fact]
    public void Pending_required_check_is_inconclusive()
    {
        var signal = TestSignals.Signal(checks: [TestSignals.Check("Build", "in_progress", null)]);
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("inconclusive", result.Outcome);
    }

    [Fact]
    public void Non_required_failure_does_not_block()
    {
        var signal = TestSignals.Signal(checks: [TestSignals.Check("optional-lint", "completed", "failure", required: false)]);
        Assert.Equal("proceed", DeterministicGates.Evaluate(signal, Options).Outcome);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("behind")]
    [InlineData("blocked")]
    [InlineData("unstable")]
    public void Non_mergeable_state_blocks(string state)
    {
        var signal = TestSignals.Signal(meta: TestSignals.Meta(mergeableState: state));
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("block", result.Outcome);
    }

    [Fact]
    public void Fork_pr_blocks_auto_merge()
    {
        var signal = TestSignals.Signal(meta: TestSignals.Meta(fromFork: true));
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("block", result.Outcome);
        Assert.Contains(result.Reasons, r => r.Contains("fork"));
    }

    [Fact]
    public void Failing_stack_layer_blocks()
    {
        var signal = TestSignals.Signal(
            stacked: true,
            stack: [new StackLayer(9, "feature/base", "main", ChecksFailing: true)]);
        var result = DeterministicGates.Evaluate(signal, Options);
        Assert.Equal("block", result.Outcome);
        Assert.Contains(result.Reasons, r => r.Contains("stack"));
    }

    [Fact]
    public void Approved_human_review_does_not_block()
    {
        var signal = TestSignals.Signal(human: [new HumanReviewSignal("reviewer", "APPROVED", DateTimeOffset.UtcNow)]);
        Assert.Equal("proceed", DeterministicGates.Evaluate(signal, Options).Outcome);
    }
}
