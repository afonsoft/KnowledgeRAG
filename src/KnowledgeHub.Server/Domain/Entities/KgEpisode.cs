namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// An episodic grouping for graph facts (SPEC-20260927-temporal-episodic-
/// knowledge-graph RF-001): one row per ingestion run that produced graph
/// facts, or per agent session — nodes/edges carry <c>EpisodeId</c> so
/// discoveries can be retrieved per episode.
/// </summary>
public sealed class KgEpisode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>"ingestion" | "agent-session".</summary>
    public required string Kind { get; set; }
    /// <summary>Source that produced the facts — null for agent sessions.</summary>
    public Guid? KnowledgeSourceId { get; set; }
    /// <summary>Conversation thread that produced the facts — null for ingestion.</summary>
    public Guid? ThreadId { get; set; }
    /// <summary>Human-readable context (e.g. "doc.md (vault/notes/doc.md)").</summary>
    public string? Summary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}
