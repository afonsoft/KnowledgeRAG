namespace KnowledgeHub.Shared.Contracts;

/// <summary>Graph node projection for the /graph UI
/// (SPEC-20260928-graph-timeline-viewer RF-001).</summary>
public sealed record GraphNodeDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public IReadOnlyList<string> Labels { get; set; } = [];
    public DateTimeOffset FirstSeenAt { get; init; }
    /// <summary>UTC — last observation by extraction/traversal.</summary>
    public DateTime ObservedAt { get; init; }
    public DateTime ValidFrom { get; init; }
    /// <summary>Null = vigente; non-null = historicized (superseded).</summary>
    public DateTime? ValidTo { get; init; }
    public string? EpisodeId { get; init; }
    public string? EpisodeSummary { get; init; }
}

/// <summary>Graph edge projection — both endpoints resolved to names.</summary>
public sealed record GraphEdgeDto
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string FromName { get; init; }
    public required string ToNodeId { get; init; }
    public required string ToName { get; init; }
    public required string Kind { get; init; }
    public double Weight { get; init; }
    public DateTime ObservedAt { get; init; }
    public DateTime ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public string? EpisodeId { get; init; }
    /// <summary>Evidence anchor — chunk + doc that back the edge.</summary>
    public string EvidenceChunkId { get; set; } = "";
    public string DocumentId { get; set; } = "";
}

/// <summary>Ingestion/session episode row.</summary>
public sealed record GraphEpisodeDto
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? KnowledgeSourceId { get; init; }
    public string? Summary { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? EndedAt { get; init; }
}

/// <summary>Paged node list response.</summary>
public sealed record GraphNodesResponse
{
    public required int Total { get; init; }
    public required IReadOnlyList<GraphNodeDto> Nodes { get; init; }
}
