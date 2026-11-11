namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-005: logical grouping of units under a
/// caller-supplied external id — the unit of delta-retain. Re-retaining the
/// same ExternalId compares per-chunk hashes (kept on the units themselves:
/// ChunkIndex + ContentHash) so unchanged chunks keep their unit ids.
/// </summary>
public sealed class MemoryDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    /// <summary>Caller-visible document id (unique per bank).</summary>
    public required string ExternalId { get; set; }
    public string? Title { get; set; }
    /// <summary>SHA-256 of the whole normalized content at last retain.</summary>
    public required string ContentHash { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public MemoryBank Bank { get; set; } = null!;
    public List<MemoryUnit> Units { get; set; } = [];
}
