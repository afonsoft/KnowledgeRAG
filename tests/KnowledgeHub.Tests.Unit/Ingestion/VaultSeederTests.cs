using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Ingestion;

/// <summary>
/// First-boot default vault seeding: <c>Vault:Path</c> config, idempotence and
/// the create-if-missing directory behavior.
/// </summary>
public sealed class VaultSeederTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly KnowledgeHubDbContext _db;

    public VaultSeederTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => KeyValuePair.Create(p.Key, p.Value)))
            .Build();

    private static string MissingTempDir() =>
        Path.Join(Path.GetTempPath(), "vault-seed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SeedAsync_NoPathConfigured_DoesNothing()
    {
        await VaultSeeder.SeedAsync(_db, Config(), NullLogger.Instance);

        Assert.Empty(_db.Sources);
    }

    [Fact]
    public async Task SeedAsync_PathConfigured_CreatesActiveObsidianVault()
    {
        var path = MissingTempDir();

        await VaultSeeder.SeedAsync(_db, Config(("Vault:Path", path)), NullLogger.Instance);

        var source = Assert.Single(_db.Sources);
        Assert.Equal(VaultSeeder.DefaultName, source.Name);
        Assert.Equal(SourceType.ObsidianVault, source.SourceType);
        Assert.True(source.IsActive);
        Assert.Contains(path, source.ConfigurationJson);
        Assert.True(Directory.Exists(path)); // seeder creates a missing root
    }

    [Fact]
    public async Task SeedAsync_CustomName_UsesConfiguredName()
    {
        var path = MissingTempDir();

        await VaultSeeder.SeedAsync(_db, Config(("Vault:Path", path), ("Vault:Name", "My Vault")), NullLogger.Instance);

        Assert.Equal("My Vault", Assert.Single(_db.Sources).Name);
    }

    [Fact]
    public async Task SeedAsync_ExistingVault_Skips()
    {
        _db.Sources.Add(new KnowledgeSource
        {
            Name = "Existing",
            SourceType = SourceType.ObsidianVault,
            ConfigurationJson = "{\"path\":\"/vaults/existing\"}"
        });
        await _db.SaveChangesAsync();

        await VaultSeeder.SeedAsync(_db, Config(("Vault:Path", MissingTempDir())), NullLogger.Instance);

        Assert.Single(_db.Sources);
    }

    [Fact]
    public async Task SeedAsync_NameTakenByOtherType_PicksUniqueName()
    {
        _db.Sources.Add(new KnowledgeSource
        {
            Name = VaultSeeder.DefaultName,
            SourceType = SourceType.WebPage,
            ConfigurationJson = "{\"url\":\"https://example.com\"}"
        });
        await _db.SaveChangesAsync();
        var path = MissingTempDir();

        await VaultSeeder.SeedAsync(_db, Config(("Vault:Path", path)), NullLogger.Instance);

        var vault = Assert.Single(_db.Sources, s => s.SourceType == SourceType.ObsidianVault);
        Assert.Equal("Default Vault (2)", vault.Name);
        Assert.Equal(2, _db.Sources.Count());
    }
}
