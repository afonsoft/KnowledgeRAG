namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// A directed relation between two <see cref="KgNode"/>s with mandatory
/// provenance — every edge traces to an evidence chunk, its document and
/// source (SPEC-20260923-graphrag RF-002: no provenance-free edges).
/// </summary>
public sealed class KgEdge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromNodeId { get; set; }
    public Guid ToNodeId { get; set; }
    /// <summary>Ontology kind: DEPENDS_ON|USES|PUBLISHED_IN|OWNED_BY|AFFECTED_BY|MENTIONS.</summary>
    public required string Kind { get; set; }
    /// <summary>The chunk the extractor cited as evidence — required.</summary>
    public Guid EvidenceChunkId { get; set; }
    public Guid KnowledgeDocumentId { get; set; }
    public Guid KnowledgeSourceId { get; set; }
    /// <summary>Extraction prompt version for future re-extraction (RF-008 guardrail).</summary>
    public string? PromptVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // SPEC-20260927-temporal-episodic-knowledge-graph RF-001: created or
    // re-observed edges get ObservedAt = now; a replaced relation keeps its
    // row with ValidTo set instead of a physical delete.
    /// <summary>Last time this relation was observed (UTC).</summary>
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Start of the validity window — defaults to first observation (UTC).</summary>
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    /// <summary>End of validity — set when superseded by fresher evidence (UTC).</summary>
    public DateTime? ValidTo { get; set; }
    /// <summary>Confidence/importance weight (default 1.0).</summary>
    public double Weight { get; set; } = 1.0;
    /// <summary>Temporal/extraction metadata as JSON (null when absent).</summary>
    public string? PropertiesJson { get; set; }
    /// <summary>Episode (ingestion run / agent session) that observed the relation.</summary>
    public Guid? EpisodeId { get; set; }

    public KgNode From { get; set; } = null!;
    public KgNode To { get; set; } = null!;
    public KnowledgeDocument Document { get; set; } = null!;
    public KgEpisode? Episode { get; set; }
}
