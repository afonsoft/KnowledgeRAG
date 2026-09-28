namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// A knowledge-graph entity extracted from a chunk (SPEC-20260923-graphrag
/// RF-002). Identity is (NormalizedName, Type) — same normalized name with a
/// different type yields a distinct node plus <see cref="KgAlias"/> rows.
/// </summary>
public sealed class KgNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Display name as extracted (first-seen casing wins).</summary>
    public required string Name { get; set; }
    /// <summary>EntityResolver-normalized form (lower, trimmed, punctuation-collapsed).</summary>
    public required string NormalizedName { get; set; }
    /// <summary>Small ontology type: service|database|api|person|team|concept.</summary>
    public required string Type { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    // SPEC-20260927-temporal-episodic-knowledge-graph RF-001: every observation
    // bumps ObservedAt; ValidFrom/ValidTo delimit the interval in which the
    // entity is considered current (soft historicization — never deleted).
    /// <summary>Last time this entity was observed by extraction/traversal writes (UTC).</summary>
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Start of the validity window — defaults to first observation (UTC).</summary>
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    /// <summary>End of validity — non-null means the entity was superseded (UTC).</summary>
    public DateTime? ValidTo { get; set; }
    /// <summary>Episode (ingestion run / agent session) that first observed it.</summary>
    public Guid? EpisodeId { get; set; }
    /// <summary>Semantic labels for diversity clustering (JSON primitive collection).</summary>
    public List<string> Labels { get; set; } = [];

    public KgEpisode? Episode { get; set; }
}
