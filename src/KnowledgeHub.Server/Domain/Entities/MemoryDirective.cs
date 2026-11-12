namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-001 (entity) /
/// SPEC-20261111-memory-reflect-directives RF-001 (usage): a standing
/// instruction attached to a bank — injected into reflect prompts and honored
/// by consolidation (e.g. "never store credentials", "prefer pt-BR
/// summaries"). <see cref="Tags"/> scopes a directive: empty = global to the
/// bank; non-empty = applies only when the reflect request tags intersect.
/// </summary>
public sealed class MemoryDirective
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    /// <summary>Optional short label for the directive.</summary>
    public string? Name { get; set; }
    public required string Content { get; set; }
    /// <summary>Ordering weight — higher applies first (default 0).</summary>
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Scope tags — empty means the directive is bank-global.</summary>
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public MemoryBank Bank { get; set; } = null!;
}
