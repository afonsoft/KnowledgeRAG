using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260922-per-key-integration-secrets RF-004 + plumbing regression:
// deepwiki is a first-class per-key integration (PUT/GET-masked/DELETE), and
// set_api_key_settings over Bearer aft_* must reach the handler — covers the
// IHttpContextAccessor registration gap that broke the tool end-to-end.
public class PerKeyIntegrationApiTests : IClassFixture<PerKeyIntegrationApiTests.Fixture>
{
    private const string DeepWikiKey = "dw-test-key-9f2c";

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"kh-perkey-{Guid.NewGuid():N}.db")
                }));
    }

    private readonly Fixture _factory;
    public PerKeyIntegrationApiTests(Fixture factory) => _factory = factory;

    private async Task<(HttpClient cookie, ApiKeyCreatedDto key)> NewKeyAsync()
    {
        var cookie = await TestAuth.LoginAsync(_factory);
        var response = await cookie.PostAsJsonAsync("/api/apikeys", new CreateApiKeyRequest("perkey-test"));
        response.EnsureSuccessStatusCode();
        return (cookie, (await response.Content.ReadFromJsonAsync<ApiKeyCreatedDto>())!);
    }

    private static bool DeepWikiHasKey(JsonElement settings)
    {
        if (!settings.TryGetProperty("integrationKeys", out var keys)
            || !keys.TryGetProperty("deepwiki", out var dw))
            return false;
        return dw.GetProperty("hasKey").GetBoolean();
    }

    [Fact]
    public async Task DeepWiki_PerKey_PutGetDelete_RoundTrip()
    {
        var (cookie, key) = await NewKeyAsync();
        var integrations = $"/api/api-keys/{key.Id}/settings/integrations/deepwiki";
        var settings = $"/api/api-keys/{key.Id}/settings/chat";

        var put = await cookie.PutAsJsonAsync(integrations, new SetIntegrationKeyRequest { ApiKey = DeepWikiKey });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain(DeepWikiKey, await put.Content.ReadAsStringAsync());

        var body = await cookie.GetStringAsync(settings);
        Assert.DoesNotContain(DeepWikiKey, body); // masked view — never the secret
        using (var doc = JsonDocument.Parse(body))
            Assert.True(DeepWikiHasKey(doc.RootElement));

        Assert.Equal(HttpStatusCode.NoContent, (await cookie.DeleteAsync(integrations)).StatusCode);

        using var after = JsonDocument.Parse(await cookie.GetStringAsync(settings));
        Assert.False(DeepWikiHasKey(after.RootElement));
    }

    [Fact]
    public async Task SetApiKeySettings_DeepWiki_BearerCaller_SavesPerKey()
    {
        var (cookie, key) = await NewKeyAsync();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", key.Key);

        var call = await bearer.PostAsJsonAsync("/api/tools/set_api_key_settings",
            new { provider = "deepwiki", apiKey = DeepWikiKey });
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);

        var body = await call.Content.ReadAsStringAsync();
        Assert.DoesNotContain(DeepWikiKey, body);
        using (var doc = JsonDocument.Parse(body))
            Assert.False(doc.RootElement.GetProperty("isError").GetBoolean());

        using var settings = JsonDocument.Parse(
            await cookie.GetStringAsync($"/api/api-keys/{key.Id}/settings/chat"));
        Assert.True(DeepWikiHasKey(settings.RootElement));
    }

    [Fact]
    public async Task SetApiKeySettings_DeepWiki_NullApiKey_RemovesPerKey()
    {
        var (cookie, key) = await NewKeyAsync();
        await cookie.PutAsJsonAsync(
            $"/api/api-keys/{key.Id}/settings/integrations/deepwiki",
            new SetIntegrationKeyRequest { ApiKey = DeepWikiKey });

        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", key.Key);
        var call = await bearer.PostAsJsonAsync("/api/tools/set_api_key_settings",
            new { provider = "deepwiki", apiKey = (string?)null });
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);

        using var settings = JsonDocument.Parse(
            await cookie.GetStringAsync($"/api/api-keys/{key.Id}/settings/chat"));
        Assert.False(DeepWikiHasKey(settings.RootElement));
    }

    // Covers SPEC-20260926-split-settings-tools RF-002: set_chat_settings
    // over Bearer aft_* saves chat endpoint and model per-key.
    [Fact]
    public async Task SetChatSettings_BearerCaller_SavesPerKey()
    {
        var (cookie, key) = await NewKeyAsync();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", key.Key);

        var call = await bearer.PostAsJsonAsync("/api/tools/set_chat_settings",
            new { endpoint = "http://localhost:11434", model = "llama3-test" });
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);

        var body = await call.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(body))
            Assert.False(doc.RootElement.GetProperty("isError").GetBoolean());

        using var settings = JsonDocument.Parse(
            await cookie.GetStringAsync($"/api/api-keys/{key.Id}/settings/chat"));
        Assert.Equal("http://localhost:11434", settings.RootElement.GetProperty("endpoint").GetString());
        Assert.Equal("llama3-test", settings.RootElement.GetProperty("model").GetString());
    }

    // Covers SPEC-20260926-split-settings-tools RF-001: set_api_key_settings
    // rejects "chat" provider.
    [Fact]
    public async Task SetApiKeySettings_ChatProvider_ReturnsError()
    {
        var (_, key) = await NewKeyAsync();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", key.Key);

        var call = await bearer.PostAsJsonAsync("/api/tools/set_api_key_settings",
            new { provider = "chat" });
        var body = await call.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task PerKeyIntegration_UnknownProvider_404()
    {
        var (cookie, key) = await NewKeyAsync();
        var response = await cookie.PutAsJsonAsync(
            $"/api/api-keys/{key.Id}/settings/integrations/not-a-provider",
            new SetIntegrationKeyRequest { ApiKey = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
