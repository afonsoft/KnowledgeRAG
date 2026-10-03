using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace KnowledgeHub.Server.Telemetry;

/// <summary>
/// SPEC-20260925-log-sinks-and-redaction RF-002: scrubs secrets from structured
/// log properties. Two layers:
/// <list type="bullet">
/// <item>Property-name match — any scalar property whose name contains
/// key/token/secret/password/connectionstring/authorization → value replaced.</item>
/// <item>Token-pattern match — scalar values containing known token prefixes
/// (aft_, ctx7sk-, sk-, fwt_, Bearer, key=..., password=...) are masked in place.</item>
/// </list>
/// Operates on top-level properties; destructured subtrees are covered because
/// their string representations land on the same ScalarValue pass. Never throws.
/// </summary>
public sealed partial class SensitiveDataEnricher : ILogEventEnricher
{
    private const string Redacted = "***REDACTED***";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "key", "token", "access_token", "secret",
        "password", "passwd", "connectionstring", "authorization",
        "x-api-key", "client_secret", "refresh_token"
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties)
        {
            if (value is not ScalarValue { Value: string s })
                continue;

            var newValue = IsSensitiveName(name)
                ? Redacted
                : ScrubValue(s);
            if (!ReferenceEquals(newValue, s) && newValue != s)
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, newValue));
        }
    }

    private static bool IsSensitiveName(string name)
    {
        if (SensitiveNames.Contains(name))
            return true;
        var lower = name.ToLowerInvariant();
        return lower.Contains("apikey") || lower.Contains("secret")
            || lower.Contains("password") || lower.Contains("token")
            || lower.Contains("connectionstring") || lower.EndsWith("authorization");
    }

    /// <summary>Masks token-shaped substrings inside a scalar value —
    /// <c>aft_…</c>, <c>ctx7sk-…</c>, <c>sk-…</c>, <c>fwt_…</c>, <c>Bearer xyz</c>,
    /// <c>Password=…</c>, <c>key=…</c> inside connection strings.</summary>
    private static string ScrubValue(string s)
    {
        var r = TokenPrefixRegex().Replace(s, Redacted);
        r = ConnStringRegex().Replace(r, "$1=" + Redacted);
        return r;
    }

    // Known app/third-party token shapes followed by their body.
    [GeneratedRegex(@"(aft_[A-Za-z0-9]+|ctx7sk-[A-Za-z0-9\-]+|sk-[A-Za-z0-9\-]{8,}|fwt_[A-Za-z0-9]+|Bearer\s+\S+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenPrefixRegex();

    // password=...; / key=...; inside a connection-string-ish payload.
    [GeneratedRegex(@"(?i)\b(password|pwd|key|secret|accesskey|accountkey)\s*=\s*[^;\s]+")]
    private static partial Regex ConnStringRegex();
}
