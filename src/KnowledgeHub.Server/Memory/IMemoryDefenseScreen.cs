using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Security;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-006 / SPEC-20261111-memory-defense:
/// screens retained content for secrets/PII before persistence — every
/// write path goes through this seam.
/// </summary>
public interface IMemoryDefenseScreen
{
    /// <summary>Returns the text to persist (redacted) or throws when blocked.</summary>
    Task<string> ScreenAsync(string text, MemoryBank bank, CancellationToken ct);
}

/// <summary>
/// SPEC-20261111-memory-defense RF-001/RF-003: screens each chunk against
/// the shared <see cref="ISecretScanner"/> with the bank's effective policy.
/// Findings are audited as <c>secret_scan</c> SecurityEvents (fingerprint
/// only — never the matched value).
/// </summary>
public sealed class MemoryDefenseScreen(
    MemoryDefense defense,
    DefensePolicyResolver resolver,
    Data.KnowledgeHubDbContext db) : IMemoryDefenseScreen
{
    public Task<string> ScreenAsync(string text, MemoryBank bank, CancellationToken ct)
    {
        var outcome = defense.Screen(text, resolver.ForBank(bank));
        if (outcome.Findings.Count == 0)
            return Task.FromResult(outcome.Text);

        foreach (var f in outcome.Findings)
            db.SecurityEvents.Add(new SecurityEvent
            {
                SourceId = null,
                ChunkIndex = -1,
                Flags = "secret_scan",
                ApiKeyId = bank.ApiKeyId == MemoryBank.ServerWideKey ? null : bank.ApiKeyId,
                Detail = $"memory_retain:{bank.BankKey}:{f.Pattern}:{f.Action.ToString().ToLowerInvariant()}:{f.Fingerprint}"
            });
        // Caller saves with the retain batch — no extra SaveChanges here.

        if (outcome.Blocked)
            throw new MemoryDefenseException(
                "retain blocked by memory defense",
                outcome.Findings.Where(f => f.Action == SecretAction.Block).ToList());
        return Task.FromResult(outcome.Text);
    }
}

/// <summary>Test seam: pass-through screen (defense off).</summary>
public sealed class NoOpMemoryDefenseScreen : IMemoryDefenseScreen
{
    public Task<string> ScreenAsync(string text, MemoryBank bank, CancellationToken ct) =>
        Task.FromResult(text);
}

/// <summary>Item blocked by a <c>block</c> policy — maps to HTTP 422.</summary>
public sealed class MemoryDefenseException(string message, IReadOnlyList<DefenseFinding> findings)
    : Exception(message)
{
    public IReadOnlyList<DefenseFinding> Findings { get; } = findings;
}
