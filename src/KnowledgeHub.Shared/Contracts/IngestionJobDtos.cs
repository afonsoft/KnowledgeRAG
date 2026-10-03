namespace KnowledgeHub.Shared.Contracts;

/// <summary>202 response from POST /api/sources/{id}/sync (queued mode).</summary>
public sealed record SyncJobEnqueueDto(Guid JobId, string Status, bool Existing);

/// <summary>GET /api/ingestion/jobs/{id} response.</summary>
public sealed record IngestionJobDto
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public int DocsProcessed { get; init; }
    public int DocsSkipped { get; init; }
    public int DocsFailed { get; init; }
    public int ChunksCreated { get; init; }
    public string? Error { get; init; }
    /// <summary>SPEC-20260926-job-error-details RF-002: per-document warnings/failures.</summary>
    public List<string>? Warnings { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}
