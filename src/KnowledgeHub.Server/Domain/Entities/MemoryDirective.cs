namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-001 (entity) /
/// SPEC-20261111-memory-reflect-directives (usage): a standing instruction
/// attached to a bank — injected into reflect prompts and honored by
/// consolidation (e.g. "never store credentials", "prefer pt-BR summaries").
/// </summary>
public sealed class MemoryDirective
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public required string Content { get; set; }
    /// <summary>Ordering weight — higher applies first (default 0).</summary>
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MemoryBank Bank { get; set; } = null!;
}
