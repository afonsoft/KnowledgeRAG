namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002 (mode=async): persisted retain
/// job — survives restarts (orphaned "running" rows are marked failed at
/// startup), feeds operations_get/cancel, and guarantees that cancellation
/// leaves no partial units (worker commits per-chunk in one transaction).
/// </summary>
public sealed class MemoryOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    /// <summary>Owning API key at enqueue time (empty = server-wide scope).</summary>
    public Guid ApiKeyId { get; set; }
    /// <summary>retain — only kind today; reserved for consolidate/reflect jobs.</summary>
    public string Kind { get; set; } = "retain";
    /// <summary>queued | running | done | failed | cancelled.</summary>
    public string Status { get; set; } = "queued";
    /// <summary>Serialized retain request (content/context/timestamp/tags/metadata/strategy).</summary>
    public required string PayloadJson { get; set; }
    /// <summary>Serialized retain result on success (unitIds, counts).</summary>
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public int UnitsCreated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public MemoryBank Bank { get; set; } = null!;
}
