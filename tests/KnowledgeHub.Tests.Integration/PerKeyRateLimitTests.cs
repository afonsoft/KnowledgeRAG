using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-per-key-rate-limits ACs: PUT/GET/DELETE round-trip on
/// /api/api-keys/{id}/rate-limit, per-key enforcement on the llm policy,
/// and clearing restoring the global limit.
/// </summary>
public class PerKeyRateLimitTests : IClassFixture<PerKeyRateLimitTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-pkrl-{Guid.NewGuid():N}.db"),
                    ["RateLimiting:LlmPermitLimit"] = "2",
                    ["RateLimiting:LlmWindowSeconds"] = "3600",
                    ["RateLimiting:GeneralPermitLimit"] = "500"
                }));
        }
    }

    private readonly Fixture _factory;
    public PerKeyRateLimitTests(Fixture factory) => _factory = factory;

    private async Task<(HttpClient Cookie, HttpClient Bearer, Guid KeyId)> NewKeyAsync()
    {
        var cookie = await TestAuth.LoginAsync(_factory);
        var name = $"pkrl-{Guid.NewGuid():N}";
        var secret = await TestAuth.CreateApiKeyAsync(cookie, name);
        var keys = await cookie.GetFromJsonAsync<List<ApiKeyDto>>("api/apikeys");
        var keyId = keys!.Single(k => k.Name == name).Id;
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return (cookie, bearer, keyId);
    }

    private static async Task<HttpStatusCode> AskAsync(HttpClient c, int i) =>
        (await c.PostAsJsonAsync("/api/ask", new { question = $"q{i}" })).StatusCode;

    [Fact]
    public async Task PutGetDelete_RoundTrip()
    {
        var (cookie, _, keyId) = await NewKeyAsync();

        var put = await cookie.PutAsJsonAsync($"/api/api-keys/{keyId}/rate-limit",
            new { llmPermits = 3, llmWindowSeconds = 120, syncPermits = (int?)null, syncWindowSeconds = (int?)null });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var keys = await cookie.GetFromJsonAsync<List<ApiKeyDto>>("api/apikeys");
        var dto = keys!.Single(k => k.Id == keyId);
        Assert.Equal(3, dto.LlmRateLimitPermits);
        Assert.Equal(120, dto.LlmRateLimitWindowSeconds);
        Assert.Null(dto.SyncRateLimitPermits);

        Assert.Equal(HttpStatusCode.NoContent,
            (await cookie.DeleteAsync($"/api/api-keys/{keyId}/rate-limit")).StatusCode);
        var cleared = (await cookie.GetFromJsonAsync<List<ApiKeyDto>>("api/apikeys"))!
            .Single(k => k.Id == keyId);
        Assert.Null(cleared.LlmRateLimitPermits);
        Assert.Null(cleared.LlmRateLimitWindowSeconds);
    }

    [Fact]
    public async Task Put_InvalidPermits_Returns400()
    {
        var (cookie, _, keyId) = await NewKeyAsync();
        var response = await cookie.PutAsJsonAsync($"/api/api-keys/{keyId}/rate-limit",
            new { llmPermits = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Override_LlmPermits1_LimitsKey_OthersKeepGlobal()
    {
        var (cookie, limited, keyId) = await NewKeyAsync();
        await cookie.PutAsJsonAsync($"/api/api-keys/{keyId}/rate-limit",
            new { llmPermits = 1, llmWindowSeconds = 3600 });

        Assert.NotEqual((HttpStatusCode)429, await AskAsync(limited, 1));
        Assert.Equal((HttpStatusCode)429, await AskAsync(limited, 2)); // override 1/hr

        var (_, other, _) = await NewKeyAsync(); // no override → global 2/hr
        Assert.NotEqual((HttpStatusCode)429, await AskAsync(other, 1));
        Assert.NotEqual((HttpStatusCode)429, await AskAsync(other, 2));
        Assert.Equal((HttpStatusCode)429, await AskAsync(other, 3));
    }

    [Fact]
    public async Task Clear_RestoresGlobal_ForSameKey()
    {
        var (cookie, bearer, keyId) = await NewKeyAsync();
        await cookie.PutAsJsonAsync($"/api/api-keys/{keyId}/rate-limit",
            new { llmPermits = 1, llmWindowSeconds = 3600 });

        Assert.NotEqual((HttpStatusCode)429, await AskAsync(bearer, 1));
        Assert.Equal((HttpStatusCode)429, await AskAsync(bearer, 2));

        // Clearing must free the key immediately — the fingerprint mechanism
        // gives it a fresh partition instead of the stale custom limiter.
        await cookie.DeleteAsync($"/api/api-keys/{keyId}/rate-limit");
        Assert.NotEqual((HttpStatusCode)429, await AskAsync(bearer, 3));
        Assert.NotEqual((HttpStatusCode)429, await AskAsync(bearer, 4));
        Assert.Equal((HttpStatusCode)429, await AskAsync(bearer, 5)); // global 2/hr again
    }
}
