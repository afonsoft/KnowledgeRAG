using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

/// <summary>Shared fixtures for the KnowledgeHub.Review.Cli tests.</summary>
internal static class TestSignals
{
    public static PrMeta Meta(
        bool isDraft = false,
        IReadOnlyList<string>? labels = null,
        string? mergeableState = "clean",
        bool fromFork = false,
        string baseRef = "main",
        string headRef = "feature/x") => new(
        Title: "Add feature X",
        Body: "Implements X.",
        Author: "octocat",
        AuthorAssociation: "MEMBER",
        IsDraft: isDraft,
        Labels: labels ?? [],
        BaseRef: baseRef,
        BaseSha: "base1",
        HeadRef: headRef,
        HeadSha: "head1",
        HeadRepoFullName: "owner/repo",
        NodeId: "PR_node1",
        HtmlUrl: "https://github.com/owner/repo/pull/1",
        MergeableState: mergeableState,
        FromFork: fromFork);

    public static PullRequestSignal Signal(
        PrMeta? meta = null,
        IReadOnlyList<ChangedFile>? files = null,
        IReadOnlyList<CheckSignal>? checks = null,
        IReadOnlyList<HumanReviewSignal>? human = null,
        bool stacked = false,
        IReadOnlyList<StackLayer>? stack = null) => new()
        {
            Repo = "owner/repo",
            Number = 1,
            Meta = meta ?? Meta(),
            Files = files ?? [File()],
            Checks = checks ?? [],
            HumanReviews = human ?? [],
            IsStacked = stacked,
            StackChain = stack ?? [],
        };

    public static ChangedFile File(string name = "src/Foo.cs", string? patch = SamplePatch, int adds = 4, int dels = 1) =>
        new(name, "modified", patch, adds, dels, null);

    /// <summary>Two-hunk patch: additions at new lines 10 and 42.</summary>
    public const string SamplePatch = """
        @@ -9,6 +9,7 @@ public class Foo
         ctx line nine
        -old call
        +new call
         ctx
         ctx
         ctx
        @@ -40,3 +41,4 @@ public class Bar
         ctx forty-one
        +added line
         ctx
         ctx
        """;

    public static CheckSignal Check(string name, string status = "completed", string? conclusion = "success", bool required = true) =>
        new(name, status, conclusion, required, "ci");
}
