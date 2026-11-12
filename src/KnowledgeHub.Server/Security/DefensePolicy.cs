using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Server.Security;

public enum SecretAction
{
    Allow,
    Redact,
    Block
}

/// <summary>
/// SPEC-20261111-memory-defense RF-002: effective policy for a scope.
/// Config shape (<c>Security:SecretScan</c> or bank/source <c>defense</c>
/// override): <c>{enabled, defaultAction, patterns:{name:action}}</c>.
/// </summary>
public sealed record DefensePolicy(
    bool Enabled,
    SecretAction DefaultAction,
    IReadOnlyDictionary<string, SecretAction> Patterns)
{
    public SecretAction ActionFor(string pattern) =>
        Patterns.TryGetValue(pattern, out var a) ? a : DefaultAction;
}

public sealed class DefensePolicyResolver(IConfiguration config)
{
    /// <summary>Weak-PII patterns default to <c>allow</c> (audit mode) — documented decision.</summary>
    private static readonly HashSet<string> WeakPii =
        ["email_pii"];

    public DefensePolicy Global()
    {
        var section = config.GetSection("Security:SecretScan");
        return Parse(section) with { };
    }

    /// <summary>Bank override: <c>MemoryBank.ConfigJson["defense"]</c>.</summary>
    public DefensePolicy ForBank(MemoryBank? bank)
    {
        var g = Global();
        var ov = OverrideFromJson(bank?.ConfigJson);
        return Merge(g, ov);
    }

    /// <summary>Source override: <c>KnowledgeSource.ConfigurationJson["defense"]</c>.</summary>
    public DefensePolicy ForSource(KnowledgeSource? source)
    {
        var g = Global();
        var ov = OverrideFromJson(source?.ConfigurationJson);
        return Merge(g, ov);
    }

    private static DefensePolicy Parse(IConfigurationSection section)
    {
        var enabled = section.GetValue("Enabled", true);
        var def = ParseAction(section["DefaultAction"]) ?? SecretAction.Redact;
        var patterns = new Dictionary<string, SecretAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in section.GetSection("Patterns").GetChildren())
            if (ParseAction(p.Value) is { } a)
                patterns[p.Key] = a;
        // Weak PII defaults: allow unless overridden.
        foreach (var w in WeakPii)
            patterns.TryAdd(w, SecretAction.Allow);
        return new DefensePolicy(enabled, def, patterns);
    }

    private static DefensePolicy? OverrideFromJson(string? json)
    {
        if (json is null or { Length: 0 })
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("defense", out var d)
                || d.ValueKind != JsonValueKind.Object)
                return null;
            return new DefensePolicy(
                ReadEnabled(d),
                ReadDefault(d),
                ReadPatterns(d));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadEnabled(JsonElement d) =>
        !d.TryGetProperty("enabled", out var e)
        || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        || e.GetBoolean();

    private static SecretAction ReadDefault(JsonElement d) =>
        d.TryGetProperty("defaultAction", out var da) && da.ValueKind == JsonValueKind.String
            ? ParseAction(da.GetString()) ?? SecretAction.Redact
            : SecretAction.Redact;

    private static Dictionary<string, SecretAction> ReadPatterns(JsonElement d)
    {
        var patterns = new Dictionary<string, SecretAction>(StringComparer.OrdinalIgnoreCase);
        if (d.TryGetProperty("patterns", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var kv in p.EnumerateObject())
                if (ParseAction(kv.Value.GetString()) is { } a)
                    patterns[kv.Name] = a;
        return patterns;
    }

    private static DefensePolicy Merge(DefensePolicy g, DefensePolicy? ov)
    {
        if (ov is null)
            return g;
        var patterns = new Dictionary<string, SecretAction>(g.Patterns, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ov.Patterns)
            patterns[k] = v;
        return ov with { Patterns = patterns };
    }

    private static SecretAction? ParseAction(string? s) => s?.ToLowerInvariant() switch
    {
        "allow" => SecretAction.Allow,
        "redact" => SecretAction.Redact,
        "block" => SecretAction.Block,
        _ => null
    };
}

public sealed record DefenseFinding(
    string Pattern, int Count, SecretAction Action, string? Fingerprint = null);
public sealed record DefenseOutcome(
    string Text,
    IReadOnlyList<DefenseFinding> Redactions,
    IReadOnlyList<DefenseFinding> Findings,
    bool Blocked);

/// <summary>
/// SPEC-20261111-memory-defense RF-003: applies the effective policy to text
/// before persistence — allow records a finding, redact rewrites with
/// <c>[REDACTED:&lt;pattern&gt;]</c>, block marks the item for rejection.
/// </summary>
public sealed class MemoryDefense(ISecretScanner scanner)
{
    public DefenseOutcome Screen(string text, DefensePolicy policy)
    {
        if (!policy.Enabled || string.IsNullOrEmpty(text))
            return new DefenseOutcome(text, [], [], false);
        var findings = scanner.Scan(text);
        if (findings.Count == 0)
            return new DefenseOutcome(text, [], [], false);

        var grouped = findings
            .GroupBy(f => f.Pattern)
            .Select(g => new DefenseFinding(
                g.Key, g.Count(), policy.ActionFor(g.Key), g.First().Fingerprint))
            .ToList();
        if (grouped.Any(f => f.Action == SecretAction.Block))
            return new DefenseOutcome(text, [], grouped, true);

        var redact = findings.Where(f => policy.ActionFor(f.Pattern) == SecretAction.Redact).ToList();
        var rewritten = scanner.Redact(text, redact);
        var redactions = grouped.Where(f => f.Action == SecretAction.Redact).ToList();
        return new DefenseOutcome(rewritten, redactions, grouped, false);
    }

    /// <summary>
    /// Redact EVERY non-allow finding (redact + block) — for text that is
    /// persisted even when an individual chunk is dropped (e.g. document
    /// <c>RawContent</c>). Invariant: nothing stored contains a non-allow
    /// pattern, so reads can never serve a blocked secret back to an LLM.
    /// </summary>
    public string RedactNonAllow(string text, DefensePolicy policy)
    {
        if (!policy.Enabled || string.IsNullOrEmpty(text))
            return text;
        var hits = scanner.Scan(text)
            .Where(f => policy.ActionFor(f.Pattern) != SecretAction.Allow)
            .ToList();
        return scanner.Redact(text, hits);
    }
}
