namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// Versioned working-context snapshot scoped to a repository
/// (SPEC-20261004-context-snapshots RF-002): one row per
/// <c>context_save</c>/<c>context_compact save=true</c> call. Append-only —
/// recovery never mutates the stored payload; retention prunes by count.
/// </summary>
public sealed class ContextSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Normalized scope <c>owner/name</c> (lowercase).</summary>
    public required string RepoKey { get; set; }
    /// <summary>Monotonic per <see cref="RepoKey"/>, starting at 1.</summary>
    public int Version { get; set; }
    /// <summary>Optional human label (e.g. <c>sprint-42</c>).</summary>
    public string? Label { get; set; }
    /// <summary>Caller-supplied dedup key — a repeated
    /// <c>context_save(repo, idempotencyKey)</c> returns the existing row
    /// instead of creating a new version.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Serialized <c>{items:[…]}</c> payload (jsonb on Postgres).</summary>
    public required string PayloadJson { get; set; }
    /// <summary>UTF-8 byte count of <see cref="PayloadJson"/>.</summary>
    public int PayloadBytes { get; set; }
    /// <summary><c>ceil(chars/4)</c> over the payload — cheap token estimate.</summary>
    public int TokenEstimate { get; set; }
    /// <summary>True when the payload was already compacted before persisting.</summary>
    public bool Compacted { get; set; }
    /// <summary><c>none</c> | <c>elision</c> | <c>elision+summary</c>.</summary>
    public required string Strategy { get; set; }
    /// <summary>Origin surface: <c>mcp</c> | <c>rest</c>.</summary>
    public required string Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Calling <see cref="ApiKey.Id"/> — informational only, no FK:
    /// a revoked/deleted key must not orphan snapshots (same contract as
    /// <see cref="EvidenceReceipt.ApiKeyId"/>).</summary>
    public Guid? CreatedByKeyId { get; set; }
}
