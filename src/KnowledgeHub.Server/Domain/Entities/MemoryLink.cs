namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-003: a typed relation between memory
/// units. <see cref="LinkType"/>: <c>temporal</c> (co-occurrence in a time
/// window, weighted), <c>causal</c> (LLM-declared "because of"), <c>semantic</c>
/// (embedding similarity ≥ threshold, opt-in). The graph arm of memory_recall
/// expands results along these edges.
/// </summary>
public sealed class MemoryLink
{
    public static readonly IReadOnlySet<string> LinkTypes =
        new HashSet<string>(StringComparer.Ordinal) { "temporal", "semantic", "causal" };

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromUnitId { get; set; }
    public Guid ToUnitId { get; set; }
    public required string LinkType { get; set; }
    /// <summary>Optional entity that motivated the link (KgNode).</summary>
    public Guid? EntityId { get; set; }
    public double Weight { get; set; } = 1.0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MemoryUnit FromUnit { get; set; } = null!;
    public MemoryUnit ToUnit { get; set; } = null!;
}
