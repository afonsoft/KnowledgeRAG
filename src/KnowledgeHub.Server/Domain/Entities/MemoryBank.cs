namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-001: an isolated agent-memory store —
/// one "brain" per user/agent/project. Identity is (BankKey, ApiKeyId);
/// <see cref="Guid.Empty"/> on ApiKeyId marks the server-wide bucket used by
/// cookie/admin sessions (EF unique indexes treat NULL inconsistently across
/// providers). Config/disposition/mission feed the reflect/consolidation
/// layers from later SPECs.
/// </summary>
public sealed class MemoryBank
{
    /// <summary>Sentinel for callers without an API key (cookie/admin).</summary>
    public static readonly Guid ServerWideKey = Guid.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Caller-visible slug (e.g. "default", "projeto-x").</summary>
    public required string BankKey { get; set; }
    /// <summary>Owning API key; <see cref="ServerWideKey"/> when unscoped.</summary>
    public Guid ApiKeyId { get; set; } = Guid.Empty;
    public required string Name { get; set; }
    /// <summary>Free-text purpose — injected into reflect prompts later.</summary>
    public string? Mission { get; set; }
    /// <summary>JSON {"skepticism":1-5,"literalism":1-5,"empathy":1-5}.</summary>
    public string? DispositionJson { get; set; }
    /// <summary>Per-bank config overrides (RF-007 hierarchy: bank > key > global).</summary>
    public string? ConfigJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<MemoryUnit> Units { get; set; } = [];
    public List<MemoryDocument> Documents { get; set; } = [];
    public List<MemoryDirective> Directives { get; set; } = [];
}
