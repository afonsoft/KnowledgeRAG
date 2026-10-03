using System.Text.Json.Nodes;

namespace KnowledgeHub.Shared.Contracts;

/// <summary>Payload to create a knowledge source.</summary>
public sealed record CreateKnowledgeSourceRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required SourceType Type { get; init; }
    public JsonObject? Configuration { get; init; }
    public bool IsActive { get; set; } = true;
    public bool AutoSyncEnabled { get; init; }
    public int? SyncIntervalMinutes { get; init; }
}

/// <summary>Payload to update a knowledge source (all fields replace existing values).</summary>
public sealed record UpdateKnowledgeSourceRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public JsonObject? Configuration { get; init; }
    public bool IsActive { get; init; }
    public bool AutoSyncEnabled { get; init; }
    public int? SyncIntervalMinutes { get; init; }
}

/// <summary>Knowledge source as returned by the API (configuration secrets redacted).</summary>
public sealed record KnowledgeSourceDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required SourceType Type { get; init; }
    public JsonObject? Configuration { get; init; }
    public required bool IsActive { get; init; }
    public required bool AutoSyncEnabled { get; init; }
    public int? SyncIntervalMinutes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    /// <summary>Outcome of the last sync run: completed | failed.</summary>
    public string? LastSyncStatus { get; init; }
    /// <summary>Reason for the last sync failure; null when healthy.</summary>
    public string? LastError { get; init; }
}

/// <summary>Indexed document summary under a source.</summary>
public sealed record KnowledgeDocumentDto
{
    public required Guid Id { get; init; }
    public required Guid SourceId { get; init; }
    public required string Title { get; init; }
    public required string UriReference { get; init; }
    public int ChunkCount { get; init; }
    public DateTimeOffset IndexedAt { get; init; }
}

/// <summary>Result of a manual or scheduled sync run (SPEC-02 RF-003 / SPEC-03 RF-004).</summary>
public sealed record SyncResultDto
{
    public required string Status { get; init; } // completed | skipped | failed
    public string? Reason { get; init; }
    public Guid? SourceId { get; init; }
    public int DocumentsProcessed { get; init; }
    public int DocumentsSkipped { get; init; }
    /// <summary>SPEC-20260924-async-ingestion-queue RF-002: per-document failures
    /// isolated from the rest of the run.</summary>
    public int DocumentsFailed { get; init; }
    public int DocumentsRemoved { get; init; }
    public int ChunksCreated { get; init; }
    public double DurationMs { get; init; }
    /// <summary>Non-fatal findings: connector item skips + security scan flags
    /// (SPEC-20260923-prompt-injection-guard RF-003).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}
