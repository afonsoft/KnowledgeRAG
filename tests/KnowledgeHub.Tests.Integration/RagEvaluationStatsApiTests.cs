using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// PR #367 follow-ups: /api/v1/evaluation/stats must work on SQLite
/// (DateTimeOffset filter evaluated in memory) and is restricted to cookie
/// sessions — flagged questions embed other users' queries, so API keys get 403.
/// </summary>
public class RagEvaluationStatsApiTests
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"kh-ragstats-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    private static async Task SeedAsync(Fixture factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        db.RagEvaluations.AddRange(
            new RagEvaluationEntity
            {
                QueryId = "recent",
                Question = "recent flagged question",
                Groundedness = 0.10,
                TimestampUtc = DateTimeOffset.UtcNow.AddHours(-2),
                FlaggedAsHallucination = true
            },
            new RagEvaluationEntity
            {
                QueryId = "stale",
                Question = "stale question outside window",
                Groundedness = 0.10,
                TimestampUtc = DateTimeOffset.UtcNow.AddDays(-30),
                FlaggedAsHallucination = true
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Stats_OnSqlite_ReturnsOnlyLast7Days()
    {
        await using var factory = new Fixture();
        var admin = await TestAuth.LoginAsync(factory);
        await SeedAsync(factory);

        var response = await admin.GetAsync("/api/v1/evaluation/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, doc.GetProperty("totalEvaluations").GetInt32());
        var flagged = doc.GetProperty("recentFlaggedQueries");
        Assert.Single(flagged.EnumerateArray());
        Assert.Equal("recent flagged question", flagged[0].GetProperty("question").GetString());
    }

    [Fact]
    public async Task Stats_WithApiKey_Denied()
    {
        await using var factory = new Fixture();
        var admin = await TestAuth.LoginAsync(factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, $"stats-{Guid.NewGuid():N}");

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        // CookieSession policy does not evaluate the apikey scheme → challenge 401
        // (same behavior as /api/apikeys for key-authenticated callers).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await bearer.GetAsync("/api/v1/evaluation/stats")).StatusCode);
    }
}
