using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.GitHub;

/// <summary>
/// Read/write surface over GitHub used by the pipeline. Octokit stays behind
/// this interface so collectors, gates and publishers are unit-testable
/// without the GitHub object graph (repo convention: hand-rolled fakes).
/// </summary>
public interface IGitHubApi
{
    Task<PrMeta> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct);

    Task<IReadOnlyList<ChangedFile>> GetPullRequestFilesAsync(string owner, string repo, int number, CancellationToken ct);

    /// <summary>Check-runs + commit statuses for <paramref name="sha"/>.</summary>
    Task<IReadOnlyList<CheckSignal>> GetChecksAsync(string owner, string repo, string sha, CancellationToken ct);

    /// <summary>Required check contexts from branch protection of <paramref name="baseRef"/>.</summary>
    Task<IReadOnlyList<string>> GetRequiredChecksAsync(string owner, string repo, string baseRef, CancellationToken ct);

    /// <summary>Issue comments + review comments on the PR.</summary>
    Task<IReadOnlyList<BotCommentSignal>> GetCommentsAsync(string owner, string repo, int number, CancellationToken ct);

    Task<IReadOnlyList<AnnotationSignal>> GetAnnotationsAsync(string owner, string repo, long checkRunId, CancellationToken ct);

    /// <summary>Check-run ids by name — needed to fetch annotations.</summary>
    Task<IReadOnlyDictionary<string, long>> GetCheckRunIdsAsync(string owner, string repo, string sha, CancellationToken ct);

    Task<IReadOnlyList<HumanReviewSignal>> GetReviewsAsync(string owner, string repo, int number, CancellationToken ct);

    /// <summary>Open PRs whose head branch is <paramref name="branchName"/> — stacked detection.</summary>
    Task<IReadOnlyList<(int Number, string HeadRef, string BaseRef)>> GetOpenPullRequestsAsync(string owner, string repo, CancellationToken ct);

    Task<string?> FindExistingSummaryCommentAsync(string owner, string repo, int number, string marker, CancellationToken ct);

    Task<long> CreateOrUpdateSummaryCommentAsync(string owner, string repo, int number, string marker, string body, CancellationToken ct);

    Task SubmitReviewAsync(string owner, string repo, int number, string commitSha, string reviewEvent,
        string? body, IReadOnlyList<InlineComment> comments, CancellationToken ct);

    Task CreateStatusAsync(string owner, string repo, string sha, string state, string context, string? description, string? targetUrl, CancellationToken ct);

    /// <summary>enablePullRequestAutoMerge via GraphQL.</summary>
    Task<bool> EnableAutoMergeAsync(string prNodeId, string mergeMethod, CancellationToken ct);

    /// <summary>Repo setting "Allow auto-merge".</summary>
    Task<bool> GetAllowAutoMergeAsync(string owner, string repo, CancellationToken ct);
}

/// <summary>
/// Inline review comment mapped onto the diff. <c>Line</c>/<c>Side</c> describe
/// the new file; <c>Position</c> is the index within the file's diff hunk that
/// the GitHub REST review API requires (Octokit only supports position-based
/// draft comments).
/// </summary>
public sealed record InlineComment(string Path, int Line, string Side, int Position, string Body);
