using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Unit.Mcp;

/// <summary>
/// Audit 2026-10-03: <see cref="EfMcpTaskStore.PurgeOlderThanAsync"/> — rows
/// past retention are deleted (the TTL only hides them from reads); rows
/// inside retention survive, terminal or not.
/// </summary>
public sealed class McpTaskStorePurgeTests : IDisposable
{
    private readonly string _dbPath =
        Path.Join(Path.GetTempPath(), $"kh-mcp-store-{Guid.NewGuid():N}.db");

    public McpTaskStorePurgeTests()
    {
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
    }

    private KnowledgeHubDbContext Open() => new(
        new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private EfMcpTaskStore Store() => new(
        new CatalogDatabase(CatalogProvider.Sqlite, null),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
            .Build());

    private async Task SeedAsync(DateTimeOffset createdAt)
    {
        await using var db = Open();
        db.McpTasks.Add(new McpTask { TaskId = $"mt_{Guid.NewGuid():N}", Status = "completed", CreatedAt = createdAt });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Purge_RemovesRowsOlderThanCutoff_KeepsRecent()
    {
        await SeedAsync(DateTimeOffset.UtcNow - TimeSpan.FromHours(100));
        await SeedAsync(DateTimeOffset.UtcNow - TimeSpan.FromHours(100));
        await SeedAsync(DateTimeOffset.UtcNow);

        var removed = await Store().PurgeOlderThanAsync(DateTimeOffset.UtcNow - TimeSpan.FromHours(72));

        Assert.Equal(2, removed);
        await using var db = Open();
        Assert.Equal(1, await db.McpTasks.CountAsync());
    }

    [Fact]
    public async Task Purge_NothingStale_ReturnsZero()
    {
        await SeedAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, await Store().PurgeOlderThanAsync(DateTimeOffset.UtcNow - TimeSpan.FromHours(72)));
    }
}
