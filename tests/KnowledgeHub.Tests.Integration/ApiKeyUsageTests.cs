using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260915-apikey-usage-audit: per-key request audit events,
// owner-only usage endpoint, 90d/10k retention, no recording for cookie
// sessions or unauthenticated requests.
public class ApiKeyUsageTests
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-usage-{Guid.NewGuid():N}.db");

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

    private static async Task<ApiKeyUsageDto?> UsageAsync(HttpClient admin, Guid keyId) =>
        await admin.GetFromJsonAsync<ApiKeyUsageDto>($"/api/apikeys/{keyId}/usage");

    private static async Task<KnowledgeHubDbContext> OpenDbAsync(Fixture factory)
    {
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        return db;
    }

    private static async Task<(HttpClient admin, string secret, Guid keyId)> NewKeyAsync(Fixture factory, string name)
    {
        var admin = await TestAuth.LoginAsync(factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, name);
        var keys = await admin.GetFromJsonAsync<List<ApiKeyDto>>("/api/apikeys");
        var keyId = keys!.Single(k => k.Name == name).Id;
        return (admin, secret, keyId);
    }

    [Fact]
    public async Task BearerRequest_RecordsUsageEvent()
    {
        // RF-002: aft_* request → 1 event with method/path/status/duration
        await using var factory = new Fixture();
        var (admin, secret, keyId) = await NewKeyAsync(factory, "audit-me");

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/sources")).StatusCode);

        var usage = await UsageAsync(admin, keyId);
        Assert.NotNull(usage);
        Assert.True(usage!.TotalCalls >= 1);
        var evt = usage.RecentEvents.First(e => e.Path == "/api/sources");
        Assert.Equal("GET", evt.HttpMethod);
        Assert.Equal(200, evt.StatusCode);
        Assert.True(evt.DurationMs >= 0);
    }

    [Fact]
    public async Task CookieRequest_RecordsNothing()
    {
        // RF-002: cookie-session requests are NOT key usage
        await using var factory = new Fixture();
        var (admin, _, keyId) = await NewKeyAsync(factory, "cookie-only");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/sources")).StatusCode);

        var usage = await UsageAsync(admin, keyId);
        Assert.NotNull(usage);
        Assert.Equal(0, usage!.TotalCalls);
    }

    [Fact]
    public async Task RevokedKey_RecordsNothing()
    {
        // revoked key → 401 → no event row
        await using var factory = new Fixture();
        var (admin, secret, keyId) = await NewKeyAsync(factory, "revoked");
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/apikeys/{keyId}")).StatusCode);

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/sources")).StatusCode);

        var usage = await UsageAsync(admin, keyId);
        Assert.NotNull(usage);
        Assert.Equal(0, usage!.TotalCalls);
    }

    [Fact]
    public async Task Usage_OtherUsersKey_Returns404()
    {
        // RF-004: owner-only — admin cannot read usage of another user's key
        await using var factory = new Fixture();
        var admin = await TestAuth.LoginAsync(factory);

        Guid foreignKeyId;
        await using (var db = await OpenDbAsync(factory))
        {
            var user = new AppUser { Username = $"user-{Guid.NewGuid():N}"[..20], PasswordHash = "x" };
            var key = new ApiKey
            {
                Name = "foreign",
                KeyHash = ApiKeyService.HashKey("aft_deadbeef"),
                Prefix = "aft_deadbeef",
                UserId = user.Id
            };
            db.Users.Add(user);
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync();
            foreignKeyId = key.Id;
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/apikeys/{foreignKeyId}/usage")).StatusCode);
    }

    [Fact]
    public async Task Usage_ApiKeyBearer_Returns401()
    {
        // RF-004/RF-010: keys cannot read key usage (cookie session only)
        await using var factory = new Fixture();
        var (_, secret, keyId) = await NewKeyAsync(factory, "no-self-read");

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await bearer.GetAsync($"/api/apikeys/{keyId}/usage")).StatusCode);
    }

    [Fact]
    public async Task OldEvents_PrunedOnInsert()
    {
        // RF-005: events older than 90d are pruned when a new event lands
        await using var factory = new Fixture();
        var (admin, secret, keyId) = await NewKeyAsync(factory, "prune-old");

        await using (var db = await OpenDbAsync(factory))
        {
            db.ApiKeyUsageEvents.AddRange(
                new ApiKeyUsageEvent
                {
                    ApiKeyId = keyId,
                    Timestamp = DateTimeOffset.UtcNow.AddDays(-100),
                    HttpMethod = "GET",
                    Path = "/api/old",
                    StatusCode = 200,
                    DurationMs = 1
                },
                new ApiKeyUsageEvent
                {
                    ApiKeyId = keyId,
                    Timestamp = DateTimeOffset.UtcNow.AddDays(-95),
                    HttpMethod = "GET",
                    Path = "/api/old2",
                    StatusCode = 200,
                    DurationMs = 1
                },
                new ApiKeyUsageEvent
                {
                    ApiKeyId = keyId,
                    Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
                    HttpMethod = "GET",
                    Path = "/api/fresh",
                    StatusCode = 200,
                    DurationMs = 1
                });
            await db.SaveChangesAsync();
        }

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/sources")).StatusCode);

        var usage = await UsageAsync(admin, keyId);
        Assert.Equal(2, usage!.TotalCalls); // fresh seeded + new request — the two 90d+ events pruned
        Assert.DoesNotContain(usage.RecentEvents, e => e.Path.StartsWith("/api/old"));
    }

    [Fact]
    public async Task Usage_Aggregates_AreCorrect()
    {
        // RF-004: totals, 24h/7d windows, error rate, avg duration
        await using var factory = new Fixture();
        var (admin, _, keyId) = await NewKeyAsync(factory, "aggregates");

        await using (var db = await OpenDbAsync(factory))
        {
            db.ApiKeyUsageEvents.AddRange(
                new ApiKeyUsageEvent { ApiKeyId = keyId, Timestamp = DateTimeOffset.UtcNow.AddHours(-2), HttpMethod = "GET", Path = "/a", StatusCode = 200, DurationMs = 100 },
                new ApiKeyUsageEvent { ApiKeyId = keyId, Timestamp = DateTimeOffset.UtcNow.AddHours(-3), HttpMethod = "GET", Path = "/b", StatusCode = 500, DurationMs = 300 },
                new ApiKeyUsageEvent { ApiKeyId = keyId, Timestamp = DateTimeOffset.UtcNow.AddDays(-3), HttpMethod = "POST", Path = "/c", StatusCode = 200, DurationMs = 200 },
                new ApiKeyUsageEvent { ApiKeyId = keyId, Timestamp = DateTimeOffset.UtcNow.AddDays(-10), HttpMethod = "GET", Path = "/d", StatusCode = 200, DurationMs = 400 });
            await db.SaveChangesAsync();
        }

        var usage = await UsageAsync(admin, keyId);
        Assert.NotNull(usage);
        Assert.Equal(4, usage!.TotalCalls);
        Assert.Equal(2, usage.CallsLast24h);
        Assert.Equal(3, usage.CallsLast7d);
        Assert.Equal(1, usage.ErrorCount);
        Assert.Equal(0.25, usage.ErrorRate, 2);
        Assert.Equal(250, usage.AvgDurationMs, 0);
        Assert.Equal(4, usage.RecentEvents.Count);
    }

    [Fact]
    public async Task Cap_PrunesOldestEvents()
    {
        // RF-005: at most 10_000 events per key — oldest dropped
        await using var factory = new Fixture();
        var (admin, secret, keyId) = await NewKeyAsync(factory, "cap");

        await using (var db = await OpenDbAsync(factory))
        {
            // Seed 10_005 recent events via recursive CTE — one statement, fast.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                WITH RECURSIVE cnt(x) AS (
                    SELECT 1 UNION ALL SELECT x+1 FROM cnt WHERE x < 10005
                )
                INSERT INTO ApiKeyUsageEvents
                    (Id, ApiKeyId, Timestamp, HttpMethod, Path, StatusCode, DurationMs, UserAgent)
                SELECT printf('%s-%s-%s-%s-%s',
                        hex(randomblob(4)), hex(randomblob(2)), hex(randomblob(2)),
                        hex(randomblob(2)), hex(randomblob(6))),
                    {keyId}, strftime('%Y-%m-%d %H:%M:%S+00:00','now','-1 hours'),
                    'GET', '/api/seed', 200, 1.0, NULL
                FROM cnt");
        }

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/sources")).StatusCode);

        var usage = await UsageAsync(admin, keyId);
        Assert.Equal(10_000, usage!.TotalCalls);
        Assert.Equal(100, usage.RecentEvents.Count);
    }
}
