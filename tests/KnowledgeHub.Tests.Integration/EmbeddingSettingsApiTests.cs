using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260926-settings-ux-embeddings RF-004: /api/settings/embeddings —
// auth, validation, masked key, store→env reset, live provider swap.
public class EmbeddingSettingsApiTests : IClassFixture<EmbeddingSettingsApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-embsettings-{Guid.NewGuid():N}.db"),
                    ["Embeddings:Provider"] = "deterministic",
                    ["Embeddings:Dimensions"] = "384"
                }));
    }

    private readonly Fixture _factory;
    public EmbeddingSettingsApiTests(Fixture factory) => _factory = factory;

    private async Task<HttpClient> AuthedCleanAsync()
    {
        var http = await TestAuth.LoginAsync(_factory);
        (await http.DeleteAsync("/api/settings/embeddings")).EnsureSuccessStatusCode();
        return http;
    }

    [Fact]
    public async Task Anonymous_Get_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/settings/embeddings")).StatusCode);
    }

    [Fact]
    public async Task Get_EnvOnly_SourceEnv_WithStampedModel()
    {
        var http = await AuthedCleanAsync();

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/embeddings"));
        var root = doc.RootElement;

        Assert.Equal("deterministic", root.GetProperty("provider").GetString());
        Assert.Equal("env", root.GetProperty("source").GetString());
        Assert.Equal(384, root.GetProperty("dimensions").GetInt32());
        Assert.Equal(500, root.GetProperty("maxTokens").GetInt32());
        Assert.Equal(50, root.GetProperty("overlapTokens").GetInt32());
        Assert.Equal("deterministic:hash384", root.GetProperty("stampedModelId").GetString());
    }

    [Fact]
    public async Task Put_Validation_RejectsBadProvider_AndBadDims()
    {
        var http = await AuthedCleanAsync();

        var badProvider = await http.PutAsJsonAsync("/api/settings/embeddings",
            new { provider = "voyage", dimensions = 384 });
        Assert.Equal(HttpStatusCode.BadRequest, badProvider.StatusCode);

        var badDims = await http.PutAsJsonAsync("/api/settings/embeddings",
            new { provider = "openai", dimensions = 8 });
        Assert.Equal(HttpStatusCode.BadRequest, badDims.StatusCode);

        var badOverlap = await http.PutAsJsonAsync("/api/settings/embeddings",
            new { provider = "deterministic", maxTokens = 100, overlapTokens = 200 });
        Assert.Equal(HttpStatusCode.BadRequest, badOverlap.StatusCode);
    }

    [Fact]
    public async Task Put_ThenGet_StoreSource_And_LiveProviderSwap()
    {
        var http = await AuthedCleanAsync();

        (await http.PutAsJsonAsync("/api/settings/embeddings", new
        {
            provider = "deterministic",
            dimensions = 512,
            maxTokens = 900,
            overlapTokens = 90,
            apiKey = "sk-emb-9e5t"
        })).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/embeddings"));
        var root = doc.RootElement;

        Assert.Equal("store", root.GetProperty("source").GetString());
        Assert.Equal(512, root.GetProperty("dimensions").GetInt32());
        Assert.Equal(900, root.GetProperty("maxTokens").GetInt32());
        Assert.Equal(90, root.GetProperty("overlapTokens").GetInt32());
        Assert.True(root.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal("store", root.GetProperty("apiKeySource").GetString());
        Assert.Equal("••••9e5t", root.GetProperty("apiKeyHint").GetString());
        // Hot-swap: the resolver rebuilt the provider for the new signature —
        // no restart needed.
        Assert.Equal("deterministic:hash512", root.GetProperty("stampedModelId").GetString());
    }

    [Fact]
    public async Task Delete_RestoresEnv()
    {
        var http = await AuthedCleanAsync();

        (await http.PutAsJsonAsync("/api/settings/embeddings",
            new { provider = "deterministic", dimensions = 384 })).EnsureSuccessStatusCode();
        (await http.DeleteAsync("/api/settings/embeddings")).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/embeddings"));
        Assert.Equal("env", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("deterministic", doc.RootElement.GetProperty("provider").GetString());
    }
}
