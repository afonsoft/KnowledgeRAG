using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Security;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Security;

/// <summary>
/// SPEC-20261111-memory-defense: scanner catalog, redaction, fingerprints
/// (salted, never the value), policy resolution (global → bank/source) and
/// boot-time rejection of hostile extra patterns.
/// </summary>
public sealed class SecretScannerTests
{
    private static SecretScanner Scanner(IConfiguration? config = null) =>
        new(config ?? Cfg(), NullLogger<SecretScanner>.Instance);

    private static IConfiguration Cfg(IDictionary<string, string?>? kv = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv ?? new Dictionary<string, string?>()).Build();

    private static DefensePolicyResolver Resolver(IConfiguration? config = null) =>
        new(config ?? Cfg());

    // ---------- RF-001 catalog ----------

    [Fact]
    public void Catalog_covers_at_least_40_named_patterns_and_all_spec_names()
    {
        var names = Scanner().Patterns;
        Assert.True(names.Count >= 40, $"expected >=40 patterns, got {names.Count}");
        foreach (var required in new[]
        {
            "github_token", "aws_access_key", "openai_api_key", "jwt", "private_key",
            "slack_token", "credit_card", "cpf", "email_pii", "password_assignment",
            "connstring_password", "bearer_token", "hub_api_key"
        })
            Assert.Contains(required, names);
    }

    [Theory]
    [InlineData("token ghp_AbCdEfGhIjKlMnOpQrSt1234", "github_token")]
    [InlineData("key = sk-proj-AbCdEfGhIjKlMnOpQrSt1234", "openai_api_key")]
    [InlineData("AKIAIOSFODNN7EXAMPLE here", "aws_access_key")]
    [InlineData("xoxb-1234567890-abcdefghijkl", "slack_token")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "jwt")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private_key")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456", "bearer_token")]
    [InlineData("password: 'supersecret123'", "password_assignment")]
    [InlineData("Server=db.local;User Id=u;Password=hunter2;Database=d", "connstring_password")]
    [InlineData("mongodb://user:pw@cluster.example.net/db", "mongodb_uri")]
    [InlineData("meu cpf é 529.982.247-25", "cpf")]
    [InlineData("mail me at jane.doe@example.com", "email_pii")]
    [InlineData("use key aft_AbCdEf1234567890xyz", "hub_api_key")]
    public void Detects_expected_pattern(string text, string pattern)
    {
        var findings = Scanner().Scan(text);
        Assert.Contains(findings, f => f.Pattern == pattern);
    }

    [Fact]
    public void Clean_text_yields_no_findings()
    {
        Assert.Empty(Scanner().Scan("O deploy no nó 3 falhou às 14h. Retentar com backoff."));
    }

    [Fact]
    public void Findings_carry_fingerprint_not_value()
    {
        const string secret = "ghp_AbCdEfGhIjKlMnOpQrSt1234";
        var f = Assert.Single(Scanner().Scan($"tok={secret}"));
        Assert.Equal("github_token", f.Pattern);
        Assert.StartsWith("fp:", f.Fingerprint);
        Assert.DoesNotContain(secret, f.Fingerprint);
        Assert.True(f.Length == secret.Length);
    }

    [Fact]
    public void Fingerprint_is_salted_and_stable()
    {
        var a = Scanner().Scan("ghp_AbCdEfGhIjKlMnOpQrSt1234").Single().Fingerprint;
        var b = Scanner(Cfg(new Dictionary<string, string?> { ["Security:SecretScan:Salt"] = "other" }))
            .Scan("ghp_AbCdEfGhIjKlMnOpQrSt1234").Single().Fingerprint;
        Assert.Equal(a, Scanner().Scan("ghp_AbCdEfGhIjKlMnOpQrSt1234").Single().Fingerprint);
        Assert.NotEqual(a, b);
    }

    // ---------- RF-003 actions ----------

    [Fact]
    public void Redact_replaces_with_marker_no_value_leak()
    {
        const string secret = "ghp_AbCdEfGhIjKlMnOpQrSt1234";
        var scanner = Scanner();
        var findings = scanner.Scan($"a {secret} b");
        var redacted = scanner.Redact($"a {secret} b", findings);
        Assert.Equal("a [REDACTED:github_token] b", redacted);
        Assert.DoesNotContain(secret, redacted);
    }

    [Fact]
    public void Defense_default_redacts_secrets_allows_weak_pii()
    {
        var defense = new MemoryDefense(Scanner());
        var outcome = defense.Screen(
            "contact jane.doe@example.com, token ghp_AbCdEfGhIjKlMnOpQrSt1234", Resolver().Global());
        Assert.False(outcome.Blocked);
        Assert.Contains("[REDACTED:github_token]", outcome.Text);
        Assert.Contains("jane.doe@example.com", outcome.Text); // weak PII default = allow
        Assert.Equal(2, outcome.Findings.Count);
        Assert.All(outcome.Findings, f => Assert.DoesNotContain("ghp_", f.Fingerprint));
    }

    [Fact]
    public void Defense_block_marks_outcome()
    {
        var cfg = Cfg(new Dictionary<string, string?>
        {
            ["Security:SecretScan:Patterns:github_token"] = "block"
        });
        var defense = new MemoryDefense(Scanner(cfg));
        var outcome = defense.Screen("ghp_AbCdEfGhIjKlMnOpQrSt1234", Resolver(cfg).Global());
        Assert.True(outcome.Blocked);
        Assert.Equal("ghp_AbCdEfGhIjKlMnOpQrSt1234", outcome.Text); // unchanged — caller discards
        Assert.DoesNotContain("[REDACTED", outcome.Text);
    }

    [Fact]
    public void Disabled_policy_is_noop()
    {
        var cfg = Cfg(new Dictionary<string, string?> { ["Security:SecretScan:Enabled"] = "false" });
        var outcome = new MemoryDefense(Scanner(cfg))
            .Screen("ghp_AbCdEfGhIjKlMnOpQrSt1234", Resolver(cfg).Global());
        Assert.False(outcome.Blocked);
        Assert.Empty(outcome.Findings);
        Assert.Equal("ghp_AbCdEfGhIjKlMnOpQrSt1234", outcome.Text);
    }

    // ---------- RF-002 overrides ----------

    [Fact]
    public void Bank_override_beats_global()
    {
        var bank = new MemoryBank
        {
            BankKey = "b1",
            Name = "B1",
            ConfigJson = """{"defense":{"patterns":{"github_token":"block"}}}"""
        };
        var policy = Resolver().ForBank(bank);
        Assert.Equal(SecretAction.Block, policy.ActionFor("github_token"));
        Assert.Equal(SecretAction.Redact, policy.ActionFor("jwt")); // global default intact
    }

    [Fact]
    public void Source_override_beats_global()
    {
        var src = new KnowledgeSource
        {
            Name = "s",
            SourceType = SourceType.ObsidianVault,
            ConfigurationJson = """{"defense":{"defaultAction":"allow","enabled":true}}"""
        };
        var policy = Resolver().ForSource(src);
        Assert.Equal(SecretAction.Allow, policy.ActionFor("github_token"));
    }

    [Fact]
    public void Malformed_override_json_falls_back_to_global()
    {
        var bank = new MemoryBank { BankKey = "b", Name = "B", ConfigJson = "{not json" };
        var policy = Resolver().ForBank(bank);
        Assert.Equal(SecretAction.Redact, policy.ActionFor("github_token"));
    }

    // ---------- RF-001 ReDoS / extra patterns ----------

    [Fact]
    public void ExtraPattern_is_registered_and_scans()
    {
        var cfg = Cfg(new Dictionary<string, string?>
        {
            ["Security:SecretScan:ExtraPatterns:acme_key"] = @"\bacme-[0-9]{8}\b"
        });
        var scanner = Scanner(cfg);
        Assert.Contains("acme_key", scanner.Patterns);
        Assert.Contains(scanner.Scan("k acme-12345678"), f => f.Pattern == "acme_key");
    }

    [Fact]
    public void Invalid_extra_pattern_fails_fast()
    {
        var cfg = Cfg(new Dictionary<string, string?>
        {
            ["Security:SecretScan:ExtraPatterns:broken"] = "([unclosed"
        });
        Assert.Throws<InvalidOperationException>(() => Scanner(cfg));
    }

    [Fact]
    public void Redos_extra_pattern_is_rejected()
    {
        var cfg = Cfg(new Dictionary<string, string?>
        {
            ["Security:SecretScan:ExtraPatterns:evil"] = "(a+)+$"
        });
        Assert.Throws<InvalidOperationException>(() => Scanner(cfg));
    }

    [Fact]
    public void Builtin_scan_survives_pathological_input_within_timeout()
    {
        var pathological = new string('a', 50_000) + "!";
        var findings = Scanner().Scan(pathological); // must not hang (MatchTimeout 100ms/pattern)
        Assert.Empty(findings);
    }
}
