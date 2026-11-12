namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-mental-models-pages RF-001: append-only version log of a
/// mental model's content — one row per refresh or manual edit.
/// </summary>
public sealed class MentalModelHistory
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public MemoryMentalModel Model { get; set; } = default!;
    public int Version { get; set; }
    public string Content { get; set; } = "";
    /// <summary>True when the version was written by a manual page edit.</summary>
    public bool ManualEdit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
