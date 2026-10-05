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

    // Choke-point tracking: writes that bypass SafeCache (direct L1L2Cache
    // and the decorated HybridCache used by EndpointCache) must land in the
    // stats key list.
    [Fact]
    public async Task Stats_Lists_L1L2_And_Hybrid_Writes()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var l1l2 = _factory.Services.GetRequiredService<L1L2Cache>();
        var hybrid = _factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        Assert.IsType<TrackingHybridCache>(hybrid);

        await l1l2.SetStringAsync("int:stats:l1l2", "v",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        await hybrid.SetAsync("int:stats:hybrid", "v",
            new Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
            ["int-tag"]);

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/settings/cache"));
        var keys = doc.RootElement.GetProperty("keys").EnumerateArray().ToList();
        Assert.Contains(keys, k => k.GetProperty("key").GetString() == "int:stats:l1l2");
        var hybridItem = Assert.Single(keys, k => k.GetProperty("key").GetString() == "int:stats:hybrid");
        Assert.Equal("int-tag", hybridItem.GetProperty("tags")[0].GetString());
    }
}
