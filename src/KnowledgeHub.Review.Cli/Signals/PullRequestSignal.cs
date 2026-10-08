namespace KnowledgeHub.Review.Signals;

/// <summary>Versioned signal payload produced by `collect` and consumed by
/// `review`/`gate`. Serialized as signal.json (schemaVersion bumps on breaks).</summary>
public sealed record PullRequestSignal
{
    public int SchemaVersion { get; init; } = 1;
    public required string Repo { get; init; }
    public required int Number { get; init; }
    public required PrMeta Meta { get; init; }
    public IReadOnlyList<ChangedFile> Files { get; init; } = [];
    public IReadOnlyList<CheckSignal> Checks { get; init; } = [];
    public IReadOnlyList<BotCommentSignal> BotComments { get; init; } = [];
    public IReadOnlyList<AnnotationSignal> Annotations { get; init; } = [];
    public IReadOnlyList<HumanReviewSignal> HumanReviews { get; init; } = [];
    public bool IsStacked { get; init; }
    public IReadOnlyList<StackLayer> StackChain { get; init; } = [];
    public bool Partial { get; init; }
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PrMeta(
    string Title,
    string? Body,
    string Author,
    string AuthorAssociation,
    bool IsDraft,
    IReadOnlyList<string> Labels,
    string BaseRef,
    string BaseSha,
    string HeadRef,
    string HeadSha,
    string HeadRepoFullName,
    string NodeId,
    string HtmlUrl,
    string? MergeableState,
    bool FromFork);

public sealed record ChangedFile(
    string Filename,
    string Status,
    string? Patch,
    int Additions,
    int Deletions,
    string? PreviousFilename);

public sealed record CheckSignal(
    string Name,
    string Status,
    string? Conclusion,
    bool IsRequired,
    string? Source);

public sealed record BotCommentSignal(
    string Author,
    string Kind,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record AnnotationSignal(
    string CheckRunName,
    string Path,
    int? StartLine,
    string Level,
    string Message);

public sealed record HumanReviewSignal(
    string Author,
    string State,
    DateTimeOffset? SubmittedAt);

public sealed record StackLayer(
    int Number,
    string HeadRef,
    string BaseRef,
    bool ChecksFailing);
