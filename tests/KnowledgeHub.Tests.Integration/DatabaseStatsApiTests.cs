using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260926-settings-tabs-database-metrics RF-002: /api/settings/database
// returns provider, file sizes, per-entity counts, migrations and vector store.
public class DatabaseStatsApiTests : IClassFixture<DatabaseStatsApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-dbstats-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
    }

    private readonly Fixture _factory;
    public DatabaseStatsApiTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_Get_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/settings/database")).StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsProvider_Counts_And_VectorStore()
    {
        var http = await TestAuth.LoginAsync(_factory);

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/database"));
        var root = doc.RootElement;

        Assert.Contains("Sqlite", root.GetProperty("provider").GetString());
        Assert.Equal(_factory.DbPath, root.GetProperty("dataSource").GetString());
        Assert.True(root.GetProperty("fileSizeBytes").GetInt64() > 0);
        Assert.True(root.GetProperty("pageCount").GetInt64() > 0);
        Assert.True(root.GetProperty("migrationsApplied").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("lastMigration").GetString()));

        var tables = root.GetProperty("tables").EnumerateArray().ToList();
        Assert.True(tables.Count > 10);
        var sources = tables.SingleOrDefault(t => t.GetProperty("name").GetString() == "Sources");
        Assert.True(sources.ValueKind != JsonValueKind.Undefined);
        Assert.True(sources.GetProperty("rowCount").GetInt64() >= 0);

        var vs = root.GetProperty("vectorStore");
        Assert.Equal(JsonValueKind.Object, vs.ValueKind);
        Assert.False(string.IsNullOrEmpty(vs.GetProperty("provider").GetString()));
        Assert.True(vs.GetProperty("dimensions").GetInt32() > 0);
    }
}
