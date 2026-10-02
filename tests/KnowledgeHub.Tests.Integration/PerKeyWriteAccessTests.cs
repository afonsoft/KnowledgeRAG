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
/// Per-key write gate: PUT /api/api-keys/{id}/write-access toggles
/// ApiKey.AllowWrite. Off → the key is read-only: write tools (ReadOnly=false)
/// stay discoverable in /api/tools but answer a friendly isError instead of
/// executing; read tools are unaffected. Cookie sessions are always
/// unrestricted. Cookie-only admin surface — keys cannot gate keys.
/// </summary>
public class PerKeyWriteAccessTests : IClassFixture<PerKeyWriteAccessTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-write-{Guid.NewGuid():N}.db");

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

    public PerKeyWriteAccessTests(Fixture factory)
    {
        _factory = factory;
        _admin = TestAuth.Login(factory);
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

    private static string ToolResultText(JsonElement result) =>
        result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    [Fact]
    public async Task WriteDisabled_ToolCallAnswersInformativeError()
    {
        var (keyId, secret) = await CreateKeyAsync("readonly-key");

        var put = await _admin.PutAsJsonAsync($"/api/api-keys/{keyId}/write-access",
            new SetApiKeyWriteAccessRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // The flag round-trips through the list DTO.
        var keys = await _admin.GetFromJsonAsync<List<ApiKeyDto>>("/api/apikeys");
        Assert.False(keys!.First(k => k.Id == keyId).AllowWrite);

        using var bearer = Bearer(secret);

        // Write tools stay discoverable in the catalog…
        var tools = await bearer.GetFromJsonAsync<ToolListResponse>("/api/tools");
        Assert.Contains(tools!.Tools, t => t.Name == "write_knowledge");

        // …but calling one answers the informative permission error.
        var denied = await bearer.PostAsJsonAsync("/api/tools/write_knowledge",
            new { title = "x", content = "y" });
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        var deniedResult = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(deniedResult.GetProperty("isError").GetBoolean());
        Assert.Contains("write access", ToolResultText(deniedResult));

        // Read tools keep working for the same key.
        var ok = await bearer.PostAsJsonAsync("/api/tools/search_knowledge", new { query = "anything" });
        ok.EnsureSuccessStatusCode();
        var okResult = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(okResult.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task WriteEnabled_Default_GateIsOpen()
    {
        var (keyId, secret) = await CreateKeyAsync("write-key");

        var keys = await _admin.GetFromJsonAsync<List<ApiKeyDto>>("/api/apikeys");
        Assert.True(keys!.First(k => k.Id == keyId).AllowWrite);

        using var bearer = Bearer(secret);

        // The call reaches the tool's own handler — no write-permission error.
        var call = await bearer.PostAsJsonAsync("/api/tools/write_knowledge",
            new { title = "t", content = "c" });
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        var result = await call.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("write access", ToolResultText(result));
    }

    [Fact]
    public async Task WriteAccess_CookieOnly()
    {
        var (_, secret) = await CreateKeyAsync("noselfwrite");
        using var bearer = Bearer(secret);

        // Keys cannot change key permissions — the cookie-only policy
        // challenges (401) or forbids (403).
        var forbidden = await bearer.PutAsJsonAsync($"/api/api-keys/{Guid.NewGuid()}/write-access",
            new SetApiKeyWriteAccessRequest(false));
        Assert.Contains(forbidden.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }
}
