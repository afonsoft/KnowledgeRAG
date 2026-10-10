using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static KnowledgeHub.Tests.Integration.TestMcp;

namespace KnowledgeHub.Tests.Integration;

// Payload caching through EndpointCache/HybridCache for the MCP + REST tool
// lists and the read-only diagnostics surfaces: keys are (catalog version,
// caller scope) shaped so scoped callers never share the unrestricted entry.
// Presence is asserted directly on the registered HybridCache — the tracked-key
// registry (CacheManagerService.Current) is a process-wide static that other
// test hosts overwrite, so asserting through it would be order-dependent.
public class ToolsPayloadCacheTests : IClassFixture<ToolsPayloadCacheTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-toolscache-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "false"
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch (IOException) { /* best effort */ }
        }
    }

    private readonly Fixture _factory;
    public ToolsPayloadCacheTests(Fixture factory) => _factory = factory;

    private HybridCache Hybrid => _factory.Services.GetRequiredService<HybridCache>();
    private long CatalogVersion => _factory.Services
        .GetRequiredService<IToolCatalogChangeNotifier>().Version;

    /// <summary>True when <paramref name="key"/> resolves without running the
    /// factory — i.e. the endpoint populated the entry.</summary>
    private async Task<bool> KeyResolvesAsync<T>(string key, T sentinel)
    {
        var value = await Hybrid.GetOrCreateAsync<T>(key,
            _ => ValueTask.FromResult(sentinel));
        return !EqualityComparer<T>.Default.Equals(value, sentinel);
    }

    private HttpClient Bearer(string secret)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    [Fact]
    public async Task RestToolsList_CachesPayload_PerScope_AndServesFromCache()
    {
        var http = await TestAuth.LoginAsync(_factory);

        var first = await http.GetFromJsonAsync<ToolListResponse>("/api/tools/");
        Assert.NotNull(first);
        var key = $"mcp:toolslist:rest:v{CatalogVersion}:*";
        Assert.True(await KeyResolvesAsync(key,
            new List<ToolDescriptorDto>
            {
                new() { Name = "__sentinel__", Description = "", InputSchema = default, ReadOnly = true }
            }));

        // A sentinel value under the key must be what the endpoint serves —
        // proving the read path goes through HybridCache, not a rebuild.
        var sentinel = new List<ToolDescriptorDto>
        {
            new() { Name = "__cached__", Description = "d", InputSchema = JsonDocument.Parse("{}").RootElement, ReadOnly = true }
        };
        await Hybrid.SetAsync(key, sentinel);
        var served = await http.GetFromJsonAsync<ToolListResponse>("/api/tools/");
        Assert.Single(served!.Tools);
        Assert.Equal("__cached__", served.Tools[0].Name);
    }

    [Fact]
    public async Task McpToolsList_CachesPayload_KeyedByVersionAndScope()
    {
        var mcp = await ConnectAsync(_factory);

        var first = ToolNames(await mcp.SendAsync("tools/list"));
        var second = ToolNames(await mcp.SendAsync("tools/list"));
        Assert.Equal(first, second);
        Assert.Contains("search_knowledge", first);

        Assert.True(await KeyResolvesAsync<List<ModelContextProtocol.Protocol.Tool>>(
            $"mcp:toolslist:v{CatalogVersion}:*", []));
    }

    [Fact]
    public async Task ScopedKey_GetsFilteredList_UnderItsOwnCacheKey()
    {
        var admin = await TestAuth.LoginAsync(_factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, "cache-scope");

        var created = await admin.GetFromJsonAsync<List<ApiKeyDto>>("/api/apikeys");
        var keyId = created!.Single(k => k.Name == "cache-scope").Id;
        (await admin.PutAsJsonAsync($"/api/api-keys/{keyId}/scopes",
            new SetApiKeyScopesRequest(null, ["search_knowledge"]))).EnsureSuccessStatusCode();

        using var bearer = Bearer(secret);
        var scoped = await bearer.GetFromJsonAsync<ToolListResponse>("/api/tools/");

        Assert.NotNull(scoped);
        var names = scoped!.Tools.Select(t => t.Name).ToList();
        Assert.Contains("search_knowledge", names);
        Assert.DoesNotContain(names, n => n.StartsWith("query_"));

        // The payload sits under the scope's own fingerprint and holds the
        // filtered list — the scoped caller never shares the "*" entry.
        var scope = new CallerScope(keyId, null,
            new HashSet<string>(["search_knowledge"], StringComparer.OrdinalIgnoreCase));
        var cached = await Hybrid.GetOrCreateAsync(
            $"mcp:toolslist:rest:v{CatalogVersion}:{scope.Fingerprint}",
            _ => ValueTask.FromResult(new List<ToolDescriptorDto>
            {
                new() { Name = "__sentinel__", Description = "", InputSchema = default, ReadOnly = true }
            }));
        Assert.Equal(names, cached.Select(t => t.Name).ToList());
    }

    [Fact]
    public async Task EvalStats_CachesPayload()
    {
        var http = await TestAuth.LoginAsync(_factory);

        var first = await http.GetStringAsync("/api/v1/evaluation/stats");
        var second = await http.GetStringAsync("/api/v1/evaluation/stats");
        Assert.Equal(first, second);

        Assert.True(await KeyResolvesAsync("eval:stats", new Server.Api.RagEvaluationStats(
            "sentinel", 0, 0, 0, 0, 0, [])));
    }

    [Fact]
    public async Task DiagnosticsVectorstore_CachesPayload_ByIndexVersion()
    {
        var http = await TestAuth.LoginAsync(_factory);

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/diagnostics/vectorstore"));
        Assert.True(doc.RootElement.TryGetProperty("provider", out _));

        var l2 = _factory.Services.GetRequiredService<IDistributedCache>();
        var version = await IndexVersionToken.GetAsync(l2, NullLogger.Instance, default);
        Assert.True(await KeyResolvesAsync(
            $"diagnostics:vectorstore:v{version}", JsonDocument.Parse("{}").RootElement));
    }
}
