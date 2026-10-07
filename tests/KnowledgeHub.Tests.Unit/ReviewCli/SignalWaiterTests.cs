using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Signals;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class SignalWaiterTests
{
    [Fact]
    public void Settled_when_all_required_checks_completed()
    {
        var signal = TestSignals.Signal(checks: [TestSignals.Check("Build"), TestSignals.Check("Lint", "completed", "success")]);
        Assert.True(SignalWaiter.Settled(signal));
    }

    [Fact]
    public void Not_settled_when_required_check_pending()
    {
        var signal = TestSignals.Signal(checks: [TestSignals.Check("Build", "in_progress", null)]);
        Assert.False(SignalWaiter.Settled(signal));
    }

    [Fact]
    public void Failed_required_check_still_settled()
    {
        // settled = terminal state, not success — the gate handles the failure.
        var signal = TestSignals.Signal(checks: [TestSignals.Check("Build", "completed", "failure")]);
        Assert.True(SignalWaiter.Settled(signal));
    }

    [Fact]
    public void Optional_pending_check_does_not_block_settled()
    {
        var signal = TestSignals.Signal(checks: [TestSignals.Check("preview", "queued", null, required: false)]);
        Assert.True(SignalWaiter.Settled(signal));
    }

    [Fact]
    public async Task Timeout_marks_signal_partial()
    {
        var options = new ReviewOptions { SignalTimeoutMin = 0 }; // immediate timeout
        var waiter = new SignalWaiter(options);
        var collectCalls = 0;
        Task<PullRequestSignal> Collect(CancellationToken _)
        {
            collectCalls++;
            return Task.FromResult(TestSignals.Signal(checks: [TestSignals.Check("Build", "in_progress", null)]));
        }
        var signal = await waiter.WaitAsync("o", "r", 1, Collect, CancellationToken.None);
        Assert.True(signal.Partial);
        Assert.Equal(1, collectCalls);
    }

    [Fact]
    public async Task Settled_signal_returns_without_polling()
    {
        var waiter = new SignalWaiter(new ReviewOptions());
        var calls = 0;
        Task<PullRequestSignal> Collect(CancellationToken _)
        {
            calls++;
            return Task.FromResult(TestSignals.Signal(checks: [TestSignals.Check("Build")]));
        }
        var signal = await waiter.WaitAsync("o", "r", 1, Collect, CancellationToken.None);
        Assert.False(signal.Partial);
        Assert.Equal(1, calls);
    }
}
