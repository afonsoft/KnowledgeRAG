using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using ModelContextProtocol;

namespace KnowledgeHub.Server.Security;

/// <summary>
/// SPEC-20261111-memory-defense RF-003/RF-004: shared screen+audit helper
/// for non-memory write paths (write_note/write_knowledge/ingest/context).
/// Audits each finding as a <c>secret_scan</c> SecurityEvent — path, pattern,
/// action and fingerprint only; never the matched value.
/// </summary>
public static class WritePathDefense
{
    /// <summary>Screen <paramref name="text"/> under the source's effective
    /// policy and persist an audit event per finding. The caller saves.</summary>
    public static DefenseOutcome Screen(
        IServiceProvider sp, KnowledgeHubDbContext db, KnowledgeSource? source,
        string text, string pathTag, Guid? apiKeyId = null)
    {
        var defense = sp.GetRequiredService<MemoryDefense>();
        var resolver = sp.GetRequiredService<DefensePolicyResolver>();
        var outcome = defense.Screen(text, resolver.ForSource(source));
        foreach (var f in outcome.Findings)
            db.SecurityEvents.Add(new SecurityEvent
            {
                SourceId = source?.Id,
                ChunkIndex = -1,
                Flags = "secret_scan",
                ApiKeyId = apiKeyId,
                Detail = $"{pathTag}:{f.Pattern}:{f.Action.ToString().ToLowerInvariant()}:{f.Fingerprint}"
            });
        return outcome;
    }

    /// <summary>
    /// context_save parity (SPEC-20261111-memory-defense RF-003): the legacy
    /// marker scan hard-failed on credential markers — the shared scanner is
    /// a detection superset, so any non-allow finding throws the same
    /// <c>secret_detected</c> error the UX already exposes.
    /// </summary>
    public static void EnsureNoSecretFindings(
        ISecretScanner scanner, DefensePolicy policy, string? value, string fieldName)
    {
        if (value is null)
            return;
        var findings = scanner.Scan(value);
        if (findings.Any(f => policy.ActionFor(f.Pattern) != SecretAction.Allow))
            throw new McpProtocolException(
                $"secret_detected: '{fieldName}' contains a credential pattern",
                McpErrorCode.InvalidParams);
    }

    public static void EnsureItemsNoSecrets(
        ISecretScanner scanner, DefensePolicy policy,
        IEnumerable<(string? Content, string Label)> fields)
    {
        foreach (var (content, label) in fields)
            EnsureNoSecretFindings(scanner, policy, content, label);
    }
}
