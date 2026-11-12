namespace KnowledgeHub.Server.Security;

/// <summary>One pattern hit — never carries the matched value.</summary>
public sealed record SecretFinding(string Pattern, int Offset, int Length, string Fingerprint);

/// <summary>
/// SPEC-20261111-memory-defense RF-001: named-pattern secret/PII scanner
/// shared by every write-path. Findings carry pattern name, span and a
/// salted fingerprint — never the raw value.
/// </summary>
public interface ISecretScanner
{
    IReadOnlyList<SecretFinding> Scan(string text);

    /// <summary>Replace every finding's span with <c>[REDACTED:&lt;pattern&gt;]</c>.</summary>
    string Redact(string text, IReadOnlyList<SecretFinding> findings);

    /// <summary>Pattern names currently registered (catalog + extras).</summary>
    IReadOnlyList<string> Patterns { get; }
}
