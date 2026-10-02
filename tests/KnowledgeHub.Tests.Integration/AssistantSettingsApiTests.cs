using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260929-a2a-assistant-delegation RF-001 / AC-1: /api/settings/assistant —
// auth, validation, persisted round-trip (key masked), store→env reset.
public class AssistantSettingsApiTests : IClassFixture<AssistantSettingsApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-asst-{Guid.NewGuid():N}.db")
                }));
        }
    }

    private readonly Fixture _factory;
    public AssistantSettingsApiTests(Fixture factory) => _factory = factory;

    private async Task<HttpClient> AuthedCleanAsync()
    {
        var http = await TestAuth.LoginAsync(_factory);
        (await http.DeleteAsync("/api/settings/assistant")).EnsureSuccessStatusCode();
        return http;
    }

    [Fact]
    public async Task Anonymous_Get_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/settings/assistant")).StatusCode);
    }

    [Fact]
    public async Task Get_NoConfig_Disabled()
    {
        var http = await AuthedCleanAsync();
        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/settings/assistant"));
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("local", doc.RootElement.GetProperty("mode").GetString());
        Assert.False(doc.RootElement.GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task Put_EnabledWithoutEndpoint_Returns400()
    {
        var http = await AuthedCleanAsync();
        var res = await http.PutAsJsonAsync("/api/settings/assistant", new
        {
            enabled = true,
            mode = "local",
            endpoint = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_InvalidMode_Returns400()
    {
        var http = await AuthedCleanAsync();
        var res = await http.PutAsJsonAsync("/api/settings/assistant", new
        {
            enabled = false,
            mode = "weird",
            endpoint = "http://x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PutThenGet_RoundTrips_KeyMasked()
    {
        var http = await AuthedCleanAsync();
        var put = await http.PutAsJsonAsync("/api/settings/assistant", new
        {
            enabled = true,
            mode = "local",
            endpoint = "https://llm.example",
            model = "gpt-4o-mini",
            route = new[] { "rewrite", "grade" },
            timeoutSeconds = 12,
            apiKey = "sk-assistant-test-1"
        });
        put.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/settings/assistant"));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("local", root.GetProperty("mode").GetString());
        Assert.Equal("https://llm.example", root.GetProperty("endpoint").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal(12, root.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal("store", root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("hasApiKey").GetBoolean());
        Assert.DoesNotContain("sk-assistant-test-1", doc.RootElement.GetRawText());
        var route = root.GetProperty("route").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "rewrite", "grade" }, route);
    }

    [Fact]
    public async Task Delete_ResetsToDefaults()
    {
        var http = await AuthedCleanAsync();
        await http.PutAsJsonAsync("/api/settings/assistant", new
        {
            enabled = true,
            mode = "local",
            endpoint = "https://llm.example",
            apiKey = "sk-x"
        });
        (await http.DeleteAsync("/api/settings/assistant")).EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/settings/assistant"));
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("hasApiKey").GetBoolean());
    }
}
