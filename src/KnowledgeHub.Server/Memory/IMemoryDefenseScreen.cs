namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-006 / SPEC-20261111-memory-defense:
/// screens retained content for secrets/PII before persistence. The no-op
/// default keeps retain functional until the defense wave lands a real
/// scanner + policies (allow/redact/block) — every write path already goes
/// through this seam.
/// </summary>
public interface IMemoryDefenseScreen
{
    /// <summary>Returns the text to persist (redacted) or throws when blocked.</summary>
    Task<string> ScreenAsync(string text, string bankKey, CancellationToken ct);
}

/// <summary>Default until SPEC-20261111-memory-defense wires the scanner.</summary>
public sealed class NoOpMemoryDefenseScreen : IMemoryDefenseScreen
{
    public Task<string> ScreenAsync(string text, string bankKey, CancellationToken ct) =>
        Task.FromResult(text);
}
