using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.GitHub;

/// <summary>
/// RF-001 — collects every review signal for a PR into a versioned
/// <see cref="PullRequestSignal"/> (signal.json). RF-002 — marks stacked PRs
/// and attaches the layer chain.
/// </summary>
public sealed class GitHubSignalsCollector(IGitHubApi api, ReviewOptions options)
{
    public async Task<PullRequestSignal> CollectAsync(string owner, string repo, int prNumber, CancellationToken ct)
    {
        var meta = await api.GetPullRequestAsync(owner, repo, prNumber, ct);
        var files = await api.GetPullRequestFilesAsync(owner, repo, prNumber, ct);
        var checks = await api.GetChecksAsync(owner, repo, meta.HeadSha, ct);
        var required = await api.GetRequiredChecksAsync(owner, repo, meta.BaseRef, ct);
        var comments = await api.GetCommentsAsync(owner, repo, prNumber, ct);
        var reviews = await api.GetReviewsAsync(owner, repo, prNumber, ct);

        var requiredSet = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase);
        checks = checks.Select(c => c with { IsRequired = requiredSet.Contains(c.Name) }).ToList();

        // Annotations only from concluded check-runs that failed/warned — keeps
        // the payload small and focused on actionable bot output.
        var checkIds = await api.GetCheckRunIdsAsync(owner, repo, meta.HeadSha, ct);
        var annotations = new List<AnnotationSignal>();
        foreach (var check in checks.Where(c =>
                     c.Source is not null && c.Source != "status" &&
                     c.Conclusion is "failure" or "neutral" or "action_required"))
        {
            if (!checkIds.TryGetValue(check.Name, out var runId))
                continue;
            var items = await api.GetAnnotationsAsync(owner, repo, runId, ct);
            annotations.AddRange(items.Select(a => a with { CheckRunName = check.Name }));
        }

        var openPrs = await api.GetOpenPullRequestsAsync(owner, repo, ct);
        var chain = StackedPrDetector.Detect(meta, openPrs, checks);

        return new PullRequestSignal
        {
            Repo = $"{owner}/{repo}",
            Number = prNumber,
            Meta = meta,
            Files = files,
            Checks = checks,
            BotComments = BotCommentParser.Filter(comments, options),
            Annotations = annotations,
            HumanReviews = reviews,
            IsStacked = chain.Count > 0,
            StackChain = chain,
        };
    }
}
