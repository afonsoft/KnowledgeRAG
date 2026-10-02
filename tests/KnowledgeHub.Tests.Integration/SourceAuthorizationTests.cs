using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-source-authorization ACs: an apikey scoped to source A sees
/// only A's chunks/tools; explicit out-of-scope source and hidden tool calls
/// fail soft (empty result / friendly isError) and write audit events; null
/// scope keeps today's unrestricted behavior.
/// </summary>
public class SourceAuthorizationTests : IClassFixture<SourceAuthorizationTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-scope-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private readonly Fixture _factory;
    private readonly HttpClient _admin;

    public SourceAuthorizationTests(Fixture factory)
    {
        _factory = factory;
        _admin = TestAuth.Login(factory);
    }

    private async Task<(Guid SourceId, string Token)> SeedSourceAsync()
    {
        var dir = Path.Join(Path.GetTempPath(), $"scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var token = $"TOK{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(Path.Join(dir, "doc.txt"), $"contents about {token}");
        var response = await _admin.PostAsJsonAsync("/api/sources", new
        {
            name = $"scope-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = dir },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        (await _admin.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null)).EnsureSuccessStatusCode();
        return (source.Id, token);
    }

    private HttpClient Bearer(string secret)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private async Task<(Guid Id, string Secret)> CreateKeyAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/apikeys", new CreateApiKeyRequest(name));
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<ApiKeyCreatedDto>())!;
        return (dto.Id, dto.Key);
    }

    [Fact]
    public async Task ScopedKey_SeesOnlyAllowedSource_ExplicitDeniedIsEmpty()
    {
        var (sourceA, tokenA) = await SeedSourceAsync();
        var (_, tokenB) = await SeedSourceAsync();
        var (keyId, secret) = await CreateKeyAsync("scoped");

        var put = await _admin.PutAsJsonAsync($"/api/api-keys/{keyId}/scopes",
            new SetApiKeyScopesRequest([sourceA], null));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var bearer = Bearer(secret);

        var inScope = await bearer.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={tokenA}&mode=lexical");
        Assert.Contains(inScope!.Results, r => r.ChunkText.Contains(tokenA));

        // Implicit fan-out never leaks B; explicit source=B also fails soft.
        var leaked = await bearer.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={tokenB}&mode=lexical");
        Assert.DoesNotContain(leaked!.Results, r => r.ChunkText.Contains(tokenB));

        var denied = await bearer.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={tokenB}&mode=lexical&sourceId={Guid.NewGuid()}");
        Assert.Empty(denied!.Results);

        // RF-005: the denial is auditable, tied to the key — never content.
        var events = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/security/events?limit=200");
        Assert.Contains(events!, e =>
            e.GetProperty("flags").GetString() == "SourceScopeDenied"
            && e.GetProperty("apiKeyId").GetString() == keyId.ToString());
    }

    [Fact]
    public async Task ScopedKey_ToolSurface_FilteredAndDeniedSoftly()
    {
        var (sourceA, _) = await SeedSourceAsync();
        var (keyId, secret) = await CreateKeyAsync("toolscope");

        (await _admin.PutAsJsonAsync($"/api/api-keys/{keyId}/scopes",
            new SetApiKeyScopesRequest([sourceA], ["search_knowledge"]))).EnsureSuccessStatusCode();

        using var bearer = Bearer(secret);
        var tools = await bearer.GetFromJsonAsync<ToolListResponse>("/api/tools");
        Assert.NotNull(tools);
        // Name allowlist wins: only search_knowledge survives (query_* filtered).
        var names = tools!.Tools.Select(t => t.Name).ToList();
        Assert.Contains("search_knowledge", names);
        Assert.DoesNotContain(names, n => n.StartsWith("query_"));

        // Hidden-but-existing tool → friendly isError, not 404, and audited.
        var denied = await bearer.PostAsJsonAsync("/api/tools/ask_knowledge", new { question = "q" });
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        var result = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("isError").GetBoolean());

        var events = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/security/events?limit=200");
        Assert.Contains(events!, e =>
            e.GetProperty("flags").GetString() == "ToolScopeDenied"
            && e.GetProperty("detail").GetString() == "ask_knowledge");

        // Allowed tool still works.
        var ok = await bearer.PostAsJsonAsync("/api/tools/search_knowledge", new { query = "anything" });
        ok.EnsureSuccessStatusCode();
        var okResult = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(okResult.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ScopeAdmin_CookieOnly_AndValidatesIds()
    {
        var (_, secret) = await CreateKeyAsync("noself");
        using var bearer = Bearer(secret);

        // Keys cannot scope keys — the cookie-only policy challenges (401) or forbids (403).
        var forbidden = await bearer.PutAsJsonAsync($"/api/api-keys/{Guid.NewGuid()}/scopes",
            new SetApiKeyScopesRequest(null, null));
        Assert.Contains(forbidden.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        await CreateKeyAsync("victim");
        var adminKeys = await _admin.GetFromJsonAsync<List<ApiKeyDto>>("/api/apikeys");
        var victimId = adminKeys!.First(k => k.Name == "victim").Id;

        // Unknown source id → 400.
        var bad = await _admin.PutAsJsonAsync($"/api/api-keys/{victimId}/scopes",
            new SetApiKeyScopesRequest([Guid.NewGuid()], null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Unknown tool name → 400.
        var badTool = await _admin.PutAsJsonAsync($"/api/api-keys/{victimId}/scopes",
            new SetApiKeyScopesRequest(null, ["no_such_tool"]));
        Assert.Equal(HttpStatusCode.BadRequest, badTool.StatusCode);
    }

    [Fact]
    public async Task NullScope_Key_Unchanged()
    {
        var (_, tokenA) = await SeedSourceAsync();
        var (_, secret) = await CreateKeyAsync("plain");
        using var bearer = Bearer(secret);

        var hits = await bearer.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={tokenA}&mode=lexical");
        Assert.Contains(hits!.Results, r => r.ChunkText.Contains(tokenA));

        var tools = await bearer.GetFromJsonAsync<ToolListResponse>("/api/tools");
        Assert.Contains(tools!.Tools, t => t.Name.StartsWith("query_"));
    }
}
