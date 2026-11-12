namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-memory-observations-consolidation RF-003: append-only audit
/// of an observation's evolution — one row per create/update decision so the
/// refinement of a belief is inspectable (and its superseded texts preserved).
/// </summary>
public sealed class ObservationHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The observation unit (MemoryUnit with FactType='observation').</summary>
    public Guid ObservationUnitId { get; set; }
    public Guid BankId { get; set; }
    /// <summary>Monotonically increasing version (1 on create).</summary>
    public int Version { get; set; }
    /// <summary>Observation text at this version.</summary>
    public required string Text { get; set; }
    /// <summary>JSON array of source unit ids added in this version.</summary>
    public required string AddedSourceIdsJson { get; set; }
    public int ProofCount { get; set; }
    /// <summary>create | update — which consolidation action wrote this row.</summary>
    public required string Action { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MemoryUnit Observation { get; set; } = null!;
}
