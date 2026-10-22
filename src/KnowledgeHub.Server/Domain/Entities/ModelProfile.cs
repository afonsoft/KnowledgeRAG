namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261021-model-capability-profiles RF-1: declared capabilities for a
/// model family (pattern-matched). User rows override the builtin catalog —
/// <see cref="Source"/> distinguishes seeded entries from user overrides.
/// </summary>
public sealed class ModelProfile
{
    public int Id { get; set; }
    /// <summary>Model-name pattern — exact name or trailing-<c>*</c> prefix
    /// (e.g. "gpt-4o*", "llama3*"). Longest match wins; "*" is the catch-all.</summary>
    public required string ModelPattern { get; set; }
    /// <summary>Prompt+completion context window in tokens (0 = unknown).</summary>
    public int ContextWindowTokens { get; set; }
    /// <summary>Provider-enforced output cap; null = use the context window.</summary>
    public int? MaxOutputTokens { get; set; }
    public bool SupportsToolCalls { get; set; }
    public bool SupportsStructuredOutput { get; set; }
    public bool SupportsReasoning { get; set; }
    /// <summary>"builtin" (seeded code catalog, read-only) or "user" (override).</summary>
    public required string Source { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
