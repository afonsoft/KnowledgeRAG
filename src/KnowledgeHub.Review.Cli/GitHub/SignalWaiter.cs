using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.GitHub;

/// <summary>
/// RF-009 — the pull_request trigger fires before review bots post, so `run`
/// can poll until required checks conclude or
/// <c>REVIEW_SIGNAL_TIMEOUT_MIN</c> elapses (partial=true then).
/// </summary>
public sealed class SignalWaiter(ReviewOptions options)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Wait until every required check concluded or timeout; returns
    /// the freshest signal. <paramref name="collect"/> re-collects each pass.</summary>
    public async Task<PullRequestSignal> WaitAsync(
        string owner, string repo, int prNumber,
        Func<CancellationToken, Task<PullRequestSignal>> collect,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(options.SignalTimeoutMin);
        var signal = await collect(ct);

        while (!Settled(signal) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);
            signal = await collect(ct);
        }

        return Settled(signal) ? signal : signal with { Partial = true };
    }

    /// <summary>True when no required check is pending and at least one bot
    /// signal or settled check exists — bots may legitimately not run at all.</summary>
    internal static bool Settled(PullRequestSignal signal) =>
        signal.Checks.Where(c => c.IsRequired).All(c =>
            c.Status is "completed" or "success");
}
