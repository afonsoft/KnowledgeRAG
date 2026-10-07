using KnowledgeHub.Review.Signals;
using Octokit;
using Octokit.GraphQL;

namespace KnowledgeHub.Review.GitHub;

/// <summary>
/// Octokit-backed <see cref="IGitHubApi"/>. Read paths use the REST client;
/// auto-merge uses Octokit.GraphQL (<c>enablePullRequestAutoMerge</c>).
/// </summary>
public sealed class OctokitGitHubApi : IGitHubApi
{
    private readonly GitHubClient _rest;
    private readonly Octokit.GraphQL.Connection _gql;

    public OctokitGitHubApi(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _rest = new GitHubClient(new Octokit.ProductHeaderValue("knowledge-review"))
        {
            Credentials = new Credentials(token)
        };
        _gql = new Octokit.GraphQL.Connection(new Octokit.GraphQL.ProductHeaderValue("knowledge-review"), token);
    }

    public async Task<PrMeta> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var pr = await _rest.PullRequest.Get(owner, repo, number).WaitAsync(ct);
        return new PrMeta(
            Title: pr.Title,
            Body: pr.Body,
            Author: pr.User.Login,
            AuthorAssociation: "unknown", // Octokit does not surface author_association on PullRequest

            IsDraft: pr.Draft,
            Labels: pr.Labels.Select(l => l.Name).ToList(),
            BaseRef: pr.Base.Ref,
            BaseSha: pr.Base.Sha,
            HeadRef: pr.Head.Ref,
            HeadSha: pr.Head.Sha,
            HeadRepoFullName: pr.Head.Repository?.FullName ?? $"{owner}/{repo}",
            NodeId: pr.NodeId,
            HtmlUrl: pr.HtmlUrl,
            MergeableState: pr.MergeableState?.StringValue,
            FromFork: pr.Head.Repository?.Fork == true);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetPullRequestFilesAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var files = await _rest.PullRequest.Files(owner, repo, number).WaitAsync(ct);
        return files.Select(f => new ChangedFile(
            f.FileName, f.Status, f.Patch, f.Additions, f.Deletions, f.PreviousFileName)).ToList();
    }

    public async Task<IReadOnlyList<CheckSignal>> GetChecksAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var checks = await _rest.Check.Run.GetAllForReference(owner, repo, sha).WaitAsync(ct);
        var statuses = await _rest.Repository.Status.GetAll(owner, repo, sha).WaitAsync(ct);

        var result = checks.CheckRuns.Select(c => new CheckSignal(
            c.Name,
            c.Status.StringValue,
            c.Conclusion?.StringValue,
            IsRequired: false, // filled by collector after branch protection lookup
            Source: c.App?.Slug)).Cast<CheckSignal>().ToList();

        result.AddRange(statuses.Select(s => new CheckSignal(
            s.Context, s.State.StringValue, Conclusion: s.State.StringValue, IsRequired: false, Source: "status")));

        return result;
    }

    public async Task<IReadOnlyList<string>> GetRequiredChecksAsync(string owner, string repo, string baseRef, CancellationToken ct)
    {
        try
        {
            var protection = await _rest.Repository.Branch.GetBranchProtection(owner, repo, baseRef).WaitAsync(ct);
            var contexts = protection?.RequiredStatusChecks?.Contexts ?? [];
            return contexts.ToList();
        }
        catch (NotFoundException)
        {
            // No branch protection or no permission — gates fall back to "all checks must pass".
            return [];
        }
    }

    public async Task<IReadOnlyList<BotCommentSignal>> GetCommentsAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var issue = await _rest.Issue.Comment.GetAllForIssue(owner, repo, number).WaitAsync(ct);
        var review = await _rest.PullRequest.ReviewComment.GetAll(owner, repo, number).WaitAsync(ct);

        var list = issue.Select(c => new BotCommentSignal(
                c.User.Login, "issue", c.Body ?? "", c.CreatedAt))
            .Concat(review.Select(c => new BotCommentSignal(
                c.User.Login, "review", c.Body ?? "", c.CreatedAt)))
            .ToList();
        return list;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetCheckRunIdsAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var checks = await _rest.Check.Run.GetAllForReference(owner, repo, sha).WaitAsync(ct);
        return checks.CheckRuns
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.First().Id);
    }

    public async Task<IReadOnlyList<AnnotationSignal>> GetAnnotationsAsync(string owner, string repo, long checkRunId, CancellationToken ct)
    {
        var annotations = await _rest.Check.Run.GetAllAnnotations(owner, repo, checkRunId).WaitAsync(ct);
        return annotations.Select(a => new AnnotationSignal(
            CheckRunName: "",
            Path: a.Path,
            StartLine: a.StartLine,
            Level: a.AnnotationLevel?.StringValue ?? "notice",
            Message: a.Message ?? "")).ToList();
    }

    public async Task<IReadOnlyList<HumanReviewSignal>> GetReviewsAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var reviews = await _rest.PullRequest.Review.GetAll(owner, repo, number).WaitAsync(ct);
        return reviews
            .Where(r => !r.User.Login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
            .Select(r => new HumanReviewSignal(r.User.Login, r.State.StringValue, r.SubmittedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<(int Number, string HeadRef, string BaseRef)>> GetOpenPullRequestsAsync(string owner, string repo, CancellationToken ct)
    {
        var prs = await _rest.PullRequest.GetAllForRepository(owner, repo,
            new PullRequestRequest { State = ItemStateFilter.Open }).WaitAsync(ct);
        return prs.Select(p => (p.Number, p.Head.Ref, p.Base.Ref)).ToList();
    }

    public async Task<string?> FindExistingSummaryCommentAsync(string owner, string repo, int number, string marker, CancellationToken ct)
    {
        var comments = await _rest.Issue.Comment.GetAllForIssue(owner, repo, number).WaitAsync(ct);
        return comments.FirstOrDefault(c => (c.Body ?? "").Contains(marker, StringComparison.Ordinal))?.Id.ToString();
    }

    public async Task<long> CreateOrUpdateSummaryCommentAsync(string owner, string repo, int number, string marker, string body, CancellationToken ct)
    {
        var comments = await _rest.Issue.Comment.GetAllForIssue(owner, repo, number).WaitAsync(ct);
        var existing = comments.FirstOrDefault(c => (c.Body ?? "").Contains(marker, StringComparison.Ordinal));
        if (existing is not null)
        {
            var updated = await _rest.Issue.Comment.Update(owner, repo, existing.Id, body).WaitAsync(ct);
            return updated.Id;
        }
        var created = await _rest.Issue.Comment.Create(owner, repo, number, body).WaitAsync(ct);
        return created.Id;
    }

    public async Task SubmitReviewAsync(string owner, string repo, int number, string commitSha, string reviewEvent,
        string? body, IReadOnlyList<InlineComment> comments, CancellationToken ct)
    {
        var review = new PullRequestReviewCreate
        {
            CommitId = commitSha,
            Event = reviewEvent switch
            {
                "APPROVE" => Octokit.PullRequestReviewEvent.Approve,
                "REQUEST_CHANGES" => Octokit.PullRequestReviewEvent.RequestChanges,
                _ => Octokit.PullRequestReviewEvent.Comment,
            },
            Body = body,
        };
        foreach (var c in comments)
            review.Comments.Add(new Octokit.DraftPullRequestReviewComment(c.Body, c.Path, c.Position));
        await _rest.PullRequest.Review.Create(owner, repo, number, review).WaitAsync(ct);
    }

    public async Task CreateStatusAsync(string owner, string repo, string sha, string state, string context, string? description, string? targetUrl, CancellationToken ct)
    {
        var status = new NewCommitStatus
        {
            State = state switch
            {
                "success" => CommitState.Success,
                "failure" => CommitState.Failure,
                "error" => CommitState.Error,
                _ => CommitState.Pending,
            },
            Context = context,
            Description = description,
            TargetUrl = targetUrl,
        };
        await _rest.Repository.Status.Create(owner, repo, sha, status).WaitAsync(ct);
    }

    public async Task<bool> EnableAutoMergeAsync(string prNodeId, string mergeMethod, CancellationToken ct)
    {
        var method = mergeMethod.ToUpperInvariant() switch
        {
            "MERGE" => Octokit.GraphQL.Model.PullRequestMergeMethod.Merge,
            "REBASE" => Octokit.GraphQL.Model.PullRequestMergeMethod.Rebase,
            _ => Octokit.GraphQL.Model.PullRequestMergeMethod.Squash,
        };
        var mutation = new Mutation()
            .EnablePullRequestAutoMerge(new Octokit.GraphQL.Model.EnablePullRequestAutoMergeInput
            {
                PullRequestId = new ID(prNodeId),
                MergeMethod = method,
            })
            .Select(x => x.ClientMutationId)
            .Compile();
        try
        {
            await _gql.Run(mutation, cancellationToken: ct);
            return true;
        }
        catch (Exception)
        {
            // Auto-merge unavailable (disabled on repo, ruleset, or token scope).
            return false;
        }
    }

    public async Task<bool> GetAllowAutoMergeAsync(string owner, string repo, CancellationToken ct)
    {
        var r = await _rest.Repository.Get(owner, repo).WaitAsync(ct);
        return r.AllowAutoMerge == true;
    }
}
