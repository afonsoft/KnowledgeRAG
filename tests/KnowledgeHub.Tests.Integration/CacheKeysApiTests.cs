using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using KnowledgeHub.Server.Caching;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260926-settings-ux-embeddings RF-003: per-key cache eviction via
// DELETE /api/settings/cache/keys/{key}.
public class CacheKeysApiTests : IClassFixture<CacheKeysApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-cachekeys-{Guid.NewGuid():N}.db")
                }));
    }

    private readonly Fixture _factory;
    public CacheKeysApiTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_Delete_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.DeleteAsync("/api/settings/cache/keys/whatever")).StatusCode);
    }

    [Fact]
    public async Task Delete_UntrackedKey_Returns404()
    {
        var http = await TestAuth.LoginAsync(_factory);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.DeleteAsync("/api/settings/cache/keys/never:tracked:key")).StatusCode);
    }

    [Fact]
    public async Task Delete_TrackedKey_RemovesEntry_AndUntracks()
    {
        var http = await TestAuth.LoginAsync(_factory);

        // Seed a tracked cache entry through the manager service.
        var cacheMgr = _factory.Services.GetRequiredService<ICacheManagerService>();
        var cache = _factory.Services.GetRequiredService<IDistributedCache>();
        const string key = "search:test:per-key-delete";
        await cache.SetStringAsync(key, "value");
        cacheMgr.TrackKey(key, 10);

        var before = await cacheMgr.GetStatsAsync();
        Assert.Contains(before.Keys, k => k.Key == key);

        Assert.Equal(HttpStatusCode.NoContent,
            (await http.DeleteAsync($"/api/settings/cache/keys/{Uri.EscapeDataString(key)}")).StatusCode);

        var after = await cacheMgr.GetStatsAsync();
        Assert.DoesNotContain(after.Keys, k => k.Key == key);
        Assert.Null(await cache.GetStringAsync(key));
    }

    [Fact]
    public async Task GetCache_StatsError_Field_Exists()
    {
        var http = await TestAuth.LoginAsync(_factory);

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/cache"));
        // statsError is part of the contract (null when healthy).
        Assert.True(doc.RootElement.TryGetProperty("statsError", out _));
    }
}
