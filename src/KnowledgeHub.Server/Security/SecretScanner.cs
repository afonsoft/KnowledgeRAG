using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Security;

/// <summary>
/// SPEC-20261111-memory-defense RF-001: catalog of named ReDoS-safe regexes
/// (each match capped by a per-pattern timeout). Custom patterns are loaded
/// from <c>Security:SecretScan:ExtraPatterns</c> and validated at boot —
/// invalid or slow patterns are rejected (fail-fast startup).
/// </summary>
public sealed partial class SecretScanner : ISecretScanner
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Built-in catalog: (name, regex, weakPii?). ~40 entries.</summary>
    private static readonly (string Name, string Regex)[] Catalog =
    [
        // ---- provider tokens ----
        ("github_token", @"\bgh[pousr]_[A-Za-z0-9]{20,}\b"),
        ("github_pat", @"\bgithub_pat_[A-Za-z0-9_]{22,}\b"),
        ("gitlab_token", @"\bglpat-[A-Za-z0-9_-]{20,}\b"),
        ("bitbucket_token", @"\bATBB[A-Za-z0-9_-]{32,}\b"),
        ("openai_api_key", @"\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}\b"),
        ("anthropic_api_key", @"\bsk-ant-[A-Za-z0-9_-]{20,}\b"),
        ("google_api_key", @"\bAIza[0-9A-Za-z_-]{35}\b"),
        ("gcp_oauth", @"\bya29\.[0-9A-Za-z_-]{20,}\b"),
        ("aws_access_key", @"\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b"),
        ("aws_secret_assignment", @"(?i)aws.{0,12}secret.{0,8}[:=]\s*[""']?[0-9a-zA-Z/+]{35,}"),
        ("azure_sas", @"\b[?&]sig=[0-9a-zA-Z%]{40,}\b"),
        ("slack_token", @"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
        ("slack_webhook", @"hooks\.slack\.com/services/T[A-Z0-9]{6,}/B[A-Z0-9]{6,}/[A-Za-z0-9]{10,}"),
        ("stripe_key", @"\b[rs]k_(?:live|test)_[A-Za-z0-9]{16,}\b"),
        ("stripe_whsec", @"\bwhsec_[A-Za-z0-9]{24,}\b"),
        ("square_token", @"\bsq0[a-z]{3}-[0-9A-Za-z_-]{22,}\b"),
        ("sendgrid_key", @"\bSG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}\b"),
        ("mailgun_key", @"\bkey-[0-9a-zA-Z]{32}\b"),
        ("mailchimp_key", @"\b[0-9a-f]{32}-us[0-9]{1,2}\b"),
        ("twilio_key", @"\bSK[0-9a-fA-F]{32}\b"),
        ("npm_token", @"\bnpm_[A-Za-z0-9]{36}\b"),
        ("pypi_token", @"\bpypi-AgEIcHlwaS5vcmc[A-Za-z0-9_-]{20,}\b"),
        ("docker_pat", @"\bdckr_pat_[A-Za-z0-9_-]{20,}\b"),
        ("heroku_key", @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\s*(?:=|:|heroku)\b"),
        ("hashicorp_vault", @"\b(?:hvs|hvb)\.[A-Za-z0-9_-]{20,}\b"),
        ("databricks_token", @"\bdapi[0-9a-fA-F]{32}\b"),
        ("supabase_key", @"\bsbp_[A-Za-z0-9]{32,}\b"),
        ("telegram_bot", @"\b\d{8,10}:[A-Za-z0-9_-]{35}\b"),
        ("discord_webhook", @"discord(?:app)?\.com/api/webhooks/\d{15,}/[A-Za-z0-9_-]{20,}"),
        ("shopify_token", @"\bshpat_[a-fA-F0-9]{32}\b"),
        ("hub_api_key", @"\baft_[A-Za-z0-9]{16,}\b"),
        ("ctx7_api_key", @"\bctx7sk-[A-Za-z0-9]{16,}\b"),

        // ---- structural secrets ----
        ("jwt", @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
        ("private_key", @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----"),
        ("ssh_public_key", @"\bssh-(?:rsa|ed25519|dss)\s+[A-Za-z0-9+/]{60,}={0,2}"),
        ("bearer_token", @"(?i)bearer\s+[A-Za-z0-9._~+/=-]{24,}"),
        ("basic_auth", @"(?i)basic\s+[A-Za-z0-9+/=]{24,}"),
        ("password_assignment", @"(?i)(?:senha|password|passwd|pwd)\s*[:=]\s*[""']?[^\s""']{8,}"),
        ("api_key_assignment", @"(?i)(?:x-api-key|api[_-]?key)\s*[:=]\s*[""']?[A-Za-z0-9_.-]{16,}"),
        ("secret_assignment", @"(?i)(?:client_?secret|app_?secret|secret_?key)\s*[:=]\s*[""']?[A-Za-z0-9_.-]{16,}"),
        ("connstring_password", @"(?i)(?:server|host|data ?source)=[^;]{3,};[^""']*(?:password|pwd)=[^;\s""']{4,}"),
        ("mongodb_uri", @"\bmongodb(?:\+srv)?://[^\s:/@]+:[^\s/@]+@"),
        ("postgres_uri", @"\b(?:postgres(?:ql)?|mysql|mariadb)://[^\s:/@]+:[^\s/@]+@"),
        ("redis_uri", @"\bredis://[^\s:/@]*:[^\s/@]+@"),
        ("amqp_uri", @"\bamqps?://[^\s:/@]+:[^\s/@]+@"),
        ("url_credentials", @"\bhttps?://[^\s/:@]+:[^\s/@]+@"),
        // bare 64-hex is too broad (SHA-256 content hashes are everywhere) —
        // require assignment context.
        ("hex_key_64", @"(?i)(?:key|secret|token)\s*[:=]\s*[""']?0x?[0-9a-fA-F]{64}\b"),

        // ---- PII / weak ----
        ("credit_card", @"\b(?:\d[ -]?){13,16}\b"),
        ("cpf", @"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b"),
        ("cnpj", @"\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b"),
        ("iban", @"\b[A-Z]{2}\d{2}[A-Z0-9]{4}\d{7}(?:[A-Z0-9]?){0,16}\b"),
        ("email_pii", @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
    ];

    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(500);

    private readonly List<(string Name, Regex Regex)> _patterns = new();
    private readonly string _salt;

    public SecretScanner(IConfiguration config, ILogger<SecretScanner> logger)
    {
        _salt = config["Security:SecretScan:Salt"] ?? "kh-secret-scan-v1";
        foreach (var (name, pattern) in Catalog)
            _patterns.Add((name, Compile(name, pattern)));
        var extras = 0;
        foreach (var child in config.GetSection("Security:SecretScan:ExtraPatterns").GetChildren())
        {
            // Boot-time validation: compile + probe; bad patterns are rejected
            // loud (fail-fast) — a hostile/slow regex can never reach runtime.
            var rx = Compile(child.Key, child.Value ?? "");
            if (SlowRegex(rx))
                throw new InvalidOperationException(
                    $"Security:SecretScan:ExtraPatterns '{child.Key}' rejected — exceeds probe budget (ReDoS risk)");
            _patterns.Add((child.Key, rx));
            extras++;
        }
        if (!config.GetValue("Security:SecretScan:Enabled", true))
            logger.LogWarning("Security:SecretScan is DISABLED — write-paths will persist secrets unscreened");
        _patternNames = _patterns.Select(p => p.Name).ToList();
        logger.LogInformation("SecretScanner loaded {Count} built-in + {Extras} extra patterns", _patterns.Count - extras, extras);
    }

    private readonly List<string> _patternNames = [];

    public IReadOnlyList<string> Patterns => _patternNames;

    public IReadOnlyList<SecretFinding> Scan(string text)
    {
        var findings = new List<SecretFinding>();
        if (string.IsNullOrEmpty(text))
            return findings;
        foreach (var (name, rx) in _patterns)
            foreach (Match m in rx.Matches(text))
                if (m.Success && m.Length > 0)
                    findings.Add(new SecretFinding(name, m.Index, m.Length, Fingerprint(m.Value)));
        return findings;
    }

    public string Redact(string text, IReadOnlyList<SecretFinding> findings)
    {
        if (findings.Count == 0)
            return text;
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var f in findings.OrderBy(f => f.Offset))
        {
            if (f.Offset < pos)
                continue; // overlapping match — first wins
            sb.Append(text, pos, f.Offset - pos);
            sb.Append("[REDACTED:").Append(f.Pattern).Append(']');
            pos = f.Offset + f.Length;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_salt + '|' + value));
        return $"fp:{Convert.ToHexStringLower(hash)[..16]}";
    }

    private static Regex Compile(string name, string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"secret-scan pattern '{name}' failed to compile", ex);
        }
    }

    /// <summary>Probe against a pathological string under a budget (ReDoS gate).</summary>
    private static bool SlowRegex(Regex rx)
    {
        var probe = new string('a', 2048) + "!" + new string('a', 2048);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            rx.IsMatch(probe);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
        return sw.Elapsed > ProbeBudget;
    }
}
