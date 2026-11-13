using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Security;

/// <summary>
/// SPEC-20261114-path-denylist RF-001: name-pattern deny-list for vault file
/// access and ingestion. Complements <see cref="SecretScanner"/> (which screens
/// content) — this screens the path itself. Defaults cover credential-shaped
/// names; <c>Security:PathAllowList</c> overrides deny matches (escape hatch).
/// </summary>
public sealed class PathDenyList
{
    // Match by path segment (dotfile-style dirs) or filename glob.
    private static readonly string[] DefaultDeny =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "*.keystore",
        "*.kubeconfig", "*.ppk", "id_rsa*", "id_ed25519*", "id_dsa*",
        ".ssh", ".aws", ".azure", ".gcp", ".gnupg", ".docker",
        "credentials*", "secrets*", "secret-*", "*-secrets",
        "api-keys*", "apikeys*", "tokens*",
    ];

    private readonly List<Regex> _deny;
    private readonly List<Regex> _allow;

    public PathDenyList(IConfiguration config)
    {
        var extra = config.GetSection("Security:PathDenyList").Get<string[]>() ?? [];
        _deny = DefaultDeny.Concat(extra).Select(ToRegex).ToList();
        _allow = (config.GetSection("Security:PathAllowList").Get<string[]>() ?? [])
            .Select(ToRegex).ToList();
    }

    /// <summary>True when the relative path (or bare filename) is denied —
    /// allow-list wins. Deny patterns match any path segment (so ".ssh" kills
    /// ".ssh/keys.md" and "credentials*" kills "notes/credentials.md").</summary>
    public bool IsDenied(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (_allow.Any(a => a.IsMatch(normalized)))
            return false;
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => _deny.Any(d => d.IsMatch(segment)));
    }

    private static Regex ToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
}
