using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260916-firecrawl-mcp-proxy RF-004/RNF-001: encrypted-at-rest
// round-trip, masked hint, removal.
public sealed class IntegrationSecretStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _services;
    private readonly MemoryCache _cache;
    private readonly IntegrationSecretStore _store;

    public IntegrationSecretStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var sc = new ServiceCollection();
        sc.AddDataProtection().UseEphemeralDataProtectionProvider();
        sc.AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite(_conn));
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>().Database.EnsureCreated();

        _store = new IntegrationSecretStore(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<IDataProtectionProvider>(),
            _cache = new MemoryCache(new MemoryCacheOptions()),
            NullLogger<IntegrationSecretStore>.Instance);
    }

    [Fact]
    public async Task SetGet_RoundTripsSecret()
    {
        await _store.SetAsync(IntegrationProviders.Firecrawl, "fc-111e2436825649da826d49a3308c839c");
        Assert.Equal("fc-111e2436825649da826d49a3308c839c",
            await _store.GetAsync(IntegrationProviders.Firecrawl));
    }

    [Fact]
    public async Task StoredValue_IsNotPlaintext()
    {
        const string secret = "fc-111e2436825649da826d49a3308c839c";
        await _store.SetAsync(IntegrationProviders.Firecrawl, secret);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var row = await db.IntegrationSecrets.SingleAsync();

        Assert.DoesNotContain(secret, row.ProtectedValue);
        Assert.Equal("839c", row.KeyHint);
    }

    [Fact]
    public async Task GetInfo_ReturnsHintNotSecret()
    {
        await _store.SetAsync(IntegrationProviders.DeepWiki, "dw-secret-abcdef");
        var info = await _store.GetInfoAsync(IntegrationProviders.DeepWiki);

        Assert.NotNull(info);
        Assert.Equal("cdef", info!.KeyHint);
        Assert.Equal(IntegrationProviders.DeepWiki, info.Provider);
    }

    [Fact]
    public async Task MissingProvider_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync(IntegrationProviders.DeepWiki));
        Assert.Null(await _store.GetInfoAsync(IntegrationProviders.DeepWiki));
        Assert.False(await _store.RemoveAsync(IntegrationProviders.DeepWiki));
    }

    [Fact]
    public async Task Remove_DeletesSecret()
    {
        await _store.SetAsync(IntegrationProviders.Firecrawl, "fc-x");
        Assert.True(await _store.RemoveAsync(IntegrationProviders.Firecrawl));
        Assert.Null(await _store.GetAsync(IntegrationProviders.Firecrawl));
    }

    [Fact]
    public async Task Set_UpsertsExistingProvider()
    {
        await _store.SetAsync(IntegrationProviders.Firecrawl, "fc-old0000");
        await _store.SetAsync(IntegrationProviders.Firecrawl, "fc-new1111");

        Assert.Equal("fc-new1111", await _store.GetAsync(IntegrationProviders.Firecrawl));

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        Assert.Equal(1, await db.IntegrationSecrets.CountAsync());
    }

    public void Dispose()
    {
        _services.Dispose();
        _cache.Dispose();
        _conn.Dispose();
    }
}
