namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-003: join between a memory unit and
/// the <see cref="KgNode"/> entities resolved from it — the queryable
/// unit↔entity mapping the graph recall arm traverses.
/// </summary>
public sealed class MemoryUnitEntity
{
    public Guid UnitId { get; set; }
    public Guid EntityId { get; set; }
    /// <summary>Denormalized for bank-scoped queries without joining units.</summary>
    public Guid BankId { get; set; }

    public MemoryUnit Unit { get; set; } = null!;
    public KgNode Entity { get; set; } = null!;
}
