namespace KnowledgeHub.Server.Memory;

/// <summary>SPEC-20261111-agent-memory-retain RF-002: retain input.</summary>
public sealed record MemoryRetainRequest(
    string Content,
    string? Context = null,
    DateTimeOffset? Timestamp = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    /// <summary>verbatim (one unit, no LLM) | extract (LLM fact extraction).</summary>
    string Strategy = "extract",
    /// <summary>Caller-supplied document id — groups units for delta-retain.</summary>
    string? DocumentId = null,
    string? DocumentTitle = null);

public sealed record MemoryRetainResult(
    Guid BankId,
    IReadOnlyList<Guid> UnitIds,
    int UnitsCreated,
    int UnitsPreserved,
    bool Async = false,
    Guid? OperationId = null,
    string? Error = null);

/// <summary>SPEC-20261111-agent-memory-retain RF-004: recall input.</summary>
public sealed record MemoryRecallRequest(
    string Query,
    int TopK = 8,
    double? MinScore = null,
    /// <summary>Temporal window ("last week", "30d"…) — enables the temporal arm.</summary>
    string? Window = null,
    /// <summary>Token budget for the returned text payload.</summary>
    int? Budget = null,
    string? FactType = null);

public sealed record RecalledMemory(
    Guid UnitId,
    string Text,
    string FactType,
    string? Context,
    DateTime? EventDate,
    DateTime MentionedAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Entities,
    double Score,
    IReadOnlyList<string> Arms);

public sealed record MemoryRecallResult(
    Guid BankId,
    IReadOnlyList<RecalledMemory> Results,
    int TotalScanned,
    IReadOnlyList<string> ArmsUsed);

public sealed record MemoryUnitDto(
    Guid Id, string Text, string FactType, string? Context,
    DateTime? EventDate, DateTime? OccurredStart, DateTime? OccurredEnd,
    DateTime MentionedAt, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string>? Metadata, string ContentHash,
    string? DocumentId, int? ChunkIndex);

public sealed record MemoryBankDto(
    Guid Id, string BankKey, string Name, string? Mission,
    string? DispositionJson, string? ConfigJson,
    DateTime CreatedAt, int UnitCount);

/// <summary>One fact extracted by the LLM in extract mode.</summary>
public sealed record ExtractedFact(
    string Text,
    string FactType,
    string? Context,
    DateTime? EventDate,
    DateTime? OccurredStart,
    DateTime? OccurredEnd,
    IReadOnlyList<ExtractedEntity> Entities);

public sealed record ExtractedEntity(string Name, string? Type);

/// <summary>Thrown for caller-fixable memory errors → maps to HTTP 400.</summary>
public sealed class MemoryBadRequestException(string message) : Exception(message);
