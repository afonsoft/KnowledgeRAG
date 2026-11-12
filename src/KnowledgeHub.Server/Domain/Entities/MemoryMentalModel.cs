namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-mental-models-pages RF-001: a standing question whose
/// synthesized answer is refreshed in background as the bank learns.
/// Subtype 'mental_model' (generic) or 'page' (opinionated defaults:
/// observations-only scope, delta refresh, wiki path, vault projection).
/// </summary>
public sealed class MemoryMentalModel
{
    public Guid Id { get; set; }
    public Guid BankId { get; set; }
    public MemoryBank Bank { get; set; } = default!;
    /// <summary>'mental_model' | 'page'.</summary>
    public required string Subtype { get; set; }
    /// <summary>Display name — unique per (BankId, Path).</summary>
    public required string Name { get; set; }
    /// <summary>Folder tree path, e.g. 'projeto/convencoes'. '' = root.</summary>
    public string Path { get; set; } = "";
    /// <summary>The permanent question this model answers.</summary>
    public required string SourceQuery { get; set; }
    /// <summary>Parsed via <see cref="MentalModelTrigger"/> — mode/refresh scoping.</summary>
    public string? TriggerJson { get; set; }
    /// <summary>Current synthesized content (markdown).</summary>
    public string Content { get; set; } = "";
    public string? StructuredContentJson { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    /// <summary>Watermark — delta refresh only folds in units newer than this.</summary>
    public DateTime? LastMemorySeenAt { get; set; }
    public string? LastRefreshError { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
