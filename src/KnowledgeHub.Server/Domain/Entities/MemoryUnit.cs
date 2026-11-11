namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-001: one typed memory fact.
/// <see cref="FactType"/>: <c>world</c> (facts about the world/domain),
/// <c>experience</c> (things the agent did/observed — first-person),
/// <c>observation</c> (consolidated conclusions — written by the consolidation
/// worker of SPEC-20261111-memory-observations-consolidation).
/// </summary>
public sealed class MemoryUnit
{
    public static readonly IReadOnlySet<string> FactTypes =
        new HashSet<string>(StringComparer.Ordinal) { "world", "experience", "observation" };

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    /// <summary>Logical document the unit was retained under (delta-retain).</summary>
    public Guid? DocumentId { get; set; }
    public int? ChunkIndex { get; set; }
    public required string Text { get; set; }
    public required string FactType { get; set; }
    /// <summary>Caller-supplied context for the fact (why it was retained).</summary>
    public string? Context { get; set; }
    /// <summary>Canonical event date extracted/declared for the fact.</summary>
    public DateTime? EventDate { get; set; }
    /// <summary>Resolved occurrence interval (relative dates expand to start/end).</summary>
    public DateTime? OccurredStart { get; set; }
    public DateTime? OccurredEnd { get; set; }
    /// <summary>When the fact was mentioned/retained (UTC).</summary>
    public DateTime MentionedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = [];
    public string? MetadataJson { get; set; }
    /// <summary>SHA-256 hex of the normalized text — dedupe + delta-retain.</summary>
    public required string ContentHash { get; set; }
    /// <summary>Stamped when a consolidation run folded this unit into an observation.</summary>
    public DateTime? ConsolidatedAt { get; set; }
    public DateTime? ConsolidationFailedAt { get; set; }
    /// <summary>Embedding blob for the semantic arm (per-bank dims guard via model).</summary>
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    /// <summary>Soft delete — memory_delete marks first; hard delete removes.</summary>
    public bool IsDeleted { get; set; }

    public MemoryBank Bank { get; set; } = null!;
    public MemoryDocument? Document { get; set; }
}
