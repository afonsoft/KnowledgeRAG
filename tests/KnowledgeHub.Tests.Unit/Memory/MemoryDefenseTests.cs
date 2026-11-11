using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Security;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261111-memory-defense RF-003/RF-004: retain write-path screening —
/// redact rewrites the unit, block rejects the item with 422-shape findings
/// and persists nothing, audit rows carry pattern/action/fingerprint only.
/// </summary>
public sealed class MemoryDefenseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"kh-def-{Guid.NewGuid():N}.db");

    public MemoryDefenseTests()
    {
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        var wal = _dbPath + "-wal";
        if (File.Exists(wal)) File.Delete(wal);
        var shm = _dbPath + "-shm";
        if (File.Exists(shm)) File.Delete(shm);
    }

    private KnowledgeHubDbContext Open() => new(
        new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private sealed class FakeScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:test";
        public int Dimensions => 8;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new float[Dimensions]);
    }

    private static IConfiguration Cfg(IDictionary<string, string?>? kv = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv ?? new Dictionary<string, string?>()).Build();

    private MemoryRetainService Retain(KnowledgeHubDbContext db, IConfiguration config)
    {
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var screen = new MemoryDefenseScreen(
            new MemoryDefense(new SecretScanner(config, NullLogger<SecretScanner>.Instance)),
            new DefensePolicyResolver(config), db);
        return new MemoryRetainService(db, banks,
            new MemoryConfigResolver(config),
            new MemoryEntityLinker(db), screen,
            new ServiceCollection().BuildServiceProvider(),
            new FakeEmbeddings(), new MemoryConsolidationTrigger(),
            NullLogger<MemoryRetainService>.Instance);
    }

    [Fact]
    public async Task RF003_retain_redacts_secret_and_audits_without_value()
    {
        using var db = Open();
        var cfg = Cfg();
        const string secret = "ghp_AbCdEfGhIjKlMnOpQrSt1234";
        var result = await Retain(db, cfg).RetainAsync(null,
            new MemoryRetainRequest($"deploy note: token {secret} rotacionado", Strategy: "verbatim"), default);

        Assert.Equal(1, result.UnitsCreated);
        var unit = await db.MemoryUnits.SingleAsync();
        Assert.Contains("[REDACTED:github_token]", unit.Text);
        Assert.DoesNotContain(secret, unit.Text);

        var ev = await db.SecurityEvents.SingleAsync();
        Assert.Equal("secret_scan", ev.Flags);
        Assert.Contains("memory_retain", ev.Detail);
        Assert.Contains("github_token", ev.Detail);
        Assert.Contains("fp:", ev.Detail);
        Assert.DoesNotContain(secret, ev.Detail);
    }

    [Fact]
    public async Task RF003_bank_block_policy_throws_and_persists_nothing()
    {
        using var db = Open();
        var cfg = Cfg();
        db.MemoryBanks.Add(new MemoryBank
        {
            BankKey = "vault",
            Name = "Vault",
            ConfigJson = """{"defense":{"defaultAction":"block"}}"""
        });
        await db.SaveChangesAsync();

        const string secret = "ghp_AbCdEfGhIjKlMnOpQrSt1234";
        var ex = await Assert.ThrowsAsync<MemoryDefenseException>(
            () => Retain(db, cfg).RetainAsync("vault",
                new MemoryRetainRequest($"token {secret}", Strategy: "verbatim"), default));

        Assert.Contains(ex.Findings, f => f.Pattern == "github_token");
        // 422-shape: findings carry pattern/count only — never spans or values.
        Assert.All(ex.Findings, f =>
        {
            Assert.False(string.IsNullOrEmpty(f.Pattern));
            Assert.DoesNotContain(secret, f.Fingerprint ?? "");
        });
        Assert.DoesNotContain(secret, ex.Message);
        Assert.Empty(await db.MemoryUnits.ToListAsync());
        // audit rows queued by the screen are discarded with the item.
        Assert.Empty(await db.SecurityEvents.ToListAsync());
    }

    [Fact]
    public async Task RF003_clean_content_passes_through_unchanged()
    {
        using var db = Open();
        var result = await Retain(db, Cfg()).RetainAsync(null,
            new MemoryRetainRequest("A migração terminou sem erros.", Strategy: "verbatim"), default);
        Assert.Equal(1, result.UnitsCreated);
        Assert.Equal("A migração terminou sem erros.", (await db.MemoryUnits.SingleAsync()).Text);
        Assert.Empty(await db.SecurityEvents.ToListAsync());
    }

    // ---------- context_save parity (RF-003) ----------

    [Fact]
    public void Context_parity_secret_throws_secret_detected()
    {
        var scanner = new SecretScanner(Cfg(), NullLogger<SecretScanner>.Instance);
        var policy = new DefensePolicyResolver(Cfg()).Global();
        var ex = Assert.Throws<ModelContextProtocol.McpProtocolException>(
            () => WritePathDefense.EnsureNoSecretFindings(
                scanner, policy, "remember token ghp_AbCdEfGhIjKlMnOpQrSt1234", "items[note]"));
        Assert.Contains("secret_detected", ex.Message);
        Assert.DoesNotContain("ghp_", ex.Message);
    }

    [Fact]
    public void Context_parity_weak_pii_allowed()
    {
        var scanner = new SecretScanner(Cfg(), NullLogger<SecretScanner>.Instance);
        var policy = new DefensePolicyResolver(Cfg()).Global();
        WritePathDefense.EnsureNoSecretFindings(
            scanner, policy, "ping jane.doe@example.com", "items[note]"); // no throw
    }

    // ---------- WritePathDefense.Screen audit (write_note/write_knowledge) ----------

    [Fact]
    public async Task WritePath_screen_audits_events_for_source()
    {
        using var db = Open();
        var src = new KnowledgeSource { Name = "v", SourceType = SourceType.ObsidianVault };
        var services = new ServiceCollection()
            .AddSingleton<ISecretScanner>(new SecretScanner(Cfg(), NullLogger<SecretScanner>.Instance))
            .AddSingleton(new DefensePolicyResolver(Cfg()))
            .AddSingleton(sp => new MemoryDefense(sp.GetRequiredService<ISecretScanner>()))
            .BuildServiceProvider();

        var outcome = WritePathDefense.Screen(
            services, db, src, "note with ghp_AbCdEfGhIjKlMnOpQrSt1234", "write_note");
        Assert.Contains("[REDACTED:github_token]", outcome.Text);
        var ev = Assert.Single(db.ChangeTracker.Entries<SecurityEvent>()).Entity;
        Assert.Equal("secret_scan", ev.Flags);
        Assert.StartsWith("write_note:", ev.Detail);
        Assert.DoesNotContain("ghp_", ev.Detail);
    }
}
