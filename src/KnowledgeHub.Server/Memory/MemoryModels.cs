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
    string? DocumentTitle = null,
    /// <summary>SPEC-20261112-memory-prompt-provenance RF-002: originating
    /// prompt/turn — defense-screened, truncated, deduped by hash.</summary>
    string? SourcePrompt = null,
    /// <summary>Caller session/thread id (map agent_chat threadId here).</summary>
    string? SessionId = null,
    /// <summary>explicit | passive | import | system (default explicit).</summary>
    string? Origin = null);

public sealed record MemoryRetainResult(
    Guid BankId,
    IReadOnlyList<Guid> UnitIds,
    int UnitsCreated,
    int UnitsPreserved,
    bool Async = false,
    Guid? OperationId = null,
    string? Error = null,
    /// <summary>SPEC-20261112-memory-prompt-provenance RF-003: same
    /// (bank, sessionId, promptHash) already retained — extraction skipped.</summary>
    bool PromptSeen = false,
    /// <summary>External id of the backing MemoryDocument when one exists.</summary>
    string? DocumentId = null);

/// <summary>SPEC-20261111-agent-memory-retain RF-004: recall input.</summary>
public sealed record MemoryRecallRequest(
    string Query,
    int TopK = 8,
    double? MinScore = null,
    /// <summary>Temporal window ("last week", "30d"…) — enables the temporal arm.</summary>
    string? Window = null,
    /// <summary>Token budget for the returned text payload.</summary>
    int? Budget = null,
    string? FactType = null,
    /// <summary>SPEC-20261112-memory-prompt-provenance RF-004: restrict to
    /// units retained under this session id ("what did we learn in session X").</summary>
    string? SessionId = null,
    /// <summary>SPEC-20261111-memory-observations-consolidation RF-006: observations
    /// supersede their source facts in the output. Default true when the bank has
    /// observations.</summary>
    bool? PreferObservations = null,
    /// <summary>Expand each returned observation's source facts {id, text}.</summary>
    bool IncludeSourceFacts = false);

public sealed record RecalledMemorySource(Guid Id, string Text);

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
    IReadOnlyList<string> Arms,
    /// <summary>Observation rows only: source fact unit ids backing it.</summary>
    IReadOnlyList<Guid>? SourceMemoryIds = null,
    int? ProofCount = null,
    /// <summary>Set when IncludeSourceFacts — expanded {id, text} of sources.</summary>
    IReadOnlyList<RecalledMemorySource>? SourceFacts = null,
    /// <summary>SPEC-20261112-memory-prompt-provenance RF-004 (preview ≤500).</summary>
    string? SourcePrompt = null,
    string? SessionId = null,
    string? Origin = null);

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
    string? DocumentId, int? ChunkIndex,
    IReadOnlyList<Guid>? SourceMemoryIds = null, int? ProofCount = null,
    /// <summary>SPEC-20261112-memory-prompt-provenance RF-004 (preview ≤500).</summary>
    string? SourcePrompt = null, string? SessionId = null, string? Origin = null);

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
public class MemoryBadRequestException(string message) : Exception(message);

/// <summary>Missing bank/unit/observation → maps to HTTP 404 in REST.</summary>
public sealed class MemoryNotFoundException(string message) : MemoryBadRequestException(message);

/// <summary>SPEC-20261111-memory-observations-consolidation RF-001: a consolidation
/// run is already active for the bank → maps to HTTP 409 in REST.</summary>
public sealed class MemoryConflictException(string message) : Exception(message);

/// <summary>SPEC-20261111-memory-observations-consolidation RF-001: run summary.</summary>
public sealed record MemoryConsolidateResult(
    Guid BankId,
    string BankKey,
    int Processed,
    int Created,
    int Updated,
    int Deleted,
    int Skipped,
    int Failed,
    bool Async = false,
    Guid? OperationId = null);

/// <summary>SPEC-20261111-memory-observations-consolidation RF-003.</summary>
public sealed record ObservationHistoryDto(
    Guid Id,
    Guid ObservationUnitId,
    int Version,
    string Text,
    IReadOnlyList<Guid> AddedSourceIds,
    int ProofCount,
    string Action,
    DateTime CreatedAt);
