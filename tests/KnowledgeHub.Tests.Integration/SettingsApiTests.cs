using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260916-firecrawl-mcp-proxy RF-005/RF-008: settings API lists both
// providers masked, saves/removes keys, never echoes the secret back.
public class SettingsApiTests : IClassFixture<SettingsApiTests.Fixture>
{
    private const string FirecrawlKey = "fc-111e2436825649da826d49a3308c839c";

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-settings-{Guid.NewGuid():N}.db")
                }));
    }

    private readonly Fixture _factory;
    public SettingsApiTests(Fixture factory) => _factory = factory;

    private static JsonElement Provider(JsonElement root, string provider) =>
        root.GetProperty("integrations").EnumerateArray()
            .Single(i => i.GetProperty("provider").GetString() == provider);

    [Fact]
    public async Task Anonymous_Get_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/settings/integrations")).StatusCode);
    }

    // SPEC-20260926-integration-toggle: disabled provider drops out of the
    // shared tools catalog (/api/tools = same DynamicToolCatalog as MCP).
    [Fact]
    public async Task Toggle_DisableProvider_RemovesItsTools()
    {
        var http = await TestAuth.LoginAsync(_factory);
        try
        {
            // baseline: enabled provider contributes tools
            using var before = JsonDocument.Parse(await http.GetStringAsync("/api/tools"));
            Assert.Contains(before.RootElement.GetProperty("tools").EnumerateArray(),
                t => t.GetProperty("name").GetString()!.StartsWith("firecrawl_"));

            var put = await http.PutAsJsonAsync(
                "/api/settings/integrations/firecrawl/enabled", new { enabled = false });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            using var list = JsonDocument.Parse(
                await http.GetStringAsync("/api/settings/integrations"));
            Assert.False(Provider(list.RootElement, "firecrawl").GetProperty("enabled").GetBoolean());

            using var after = JsonDocument.Parse(await http.GetStringAsync("/api/tools"));
            Assert.DoesNotContain(after.RootElement.GetProperty("tools").EnumerateArray(),
                t => t.GetProperty("name").GetString()!.StartsWith("firecrawl_"));
        }
        finally
        {
            await http.PutAsJsonAsync(
                "/api/settings/integrations/firecrawl/enabled", new { enabled = true });
        }
    }

    [Fact]
    public async Task Toggle_UnknownProvider_Returns404()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var put = await http.PutAsJsonAsync(
            "/api/settings/integrations/nope/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task List_BothProviders_KeylessAfterRemoval()
    {
        var http = await TestAuth.LoginAsync(_factory);
        // Shared fixture DB — order-independent: clear any leftover keys first.
        await http.DeleteAsync("/api/settings/integrations/firecrawl");
        await http.DeleteAsync("/api/settings/integrations/deepwiki");

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/integrations"));

        var fc = Provider(doc.RootElement, "firecrawl");
        var dw = Provider(doc.RootElement, "deepwiki");

        Assert.False(fc.GetProperty("hasKey").GetBoolean());
        Assert.False(dw.GetProperty("hasKey").GetBoolean());
        Assert.Equal("none", fc.GetProperty("source").GetString());
        Assert.Equal("none", dw.GetProperty("source").GetString());
    }

    [Fact]
    public async Task PutThenGet_MaskedHint_NeverEchoesSecret()
    {
        var http = await TestAuth.LoginAsync(_factory);

        var put = await http.PutAsJsonAsync("/api/settings/integrations/firecrawl",
            new { apiKey = FirecrawlKey });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var body = await http.GetStringAsync("/api/settings/integrations");
        Assert.DoesNotContain(FirecrawlKey, body); // RNF-001: secret never leaves the server

        using var doc = JsonDocument.Parse(body);
        var fc = Provider(doc.RootElement, "firecrawl");
        Assert.True(fc.GetProperty("hasKey").GetBoolean());
        Assert.Equal("store", fc.GetProperty("source").GetString());
        Assert.Contains("839c", fc.GetProperty("keyHint").GetString());
    }

    [Fact]
    public async Task Put_FirecrawlKeyWithoutFcPrefix_400()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var put = await http.PutAsJsonAsync("/api/settings/integrations/firecrawl",
            new { apiKey = "sk-not-firecrawl" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_UnknownProvider_404()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var put = await http.PutAsJsonAsync("/api/settings/integrations/ghost",
            new { apiKey = "whatever" });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Put_DeepWikiKey_AnyNonEmpty_204()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var put = await http.PutAsJsonAsync("/api/settings/integrations/deepwiki",
            new { apiKey = "dw-private-key-42" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/integrations"));
        var dw = Provider(doc.RootElement, "deepwiki");
        Assert.True(dw.GetProperty("hasKey").GetBoolean());
        Assert.Equal("store", dw.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Delete_RemovesStoredKey()
    {
        var http = await TestAuth.LoginAsync(_factory);
        await http.PutAsJsonAsync("/api/settings/integrations/firecrawl",
            new { apiKey = FirecrawlKey });

        Assert.Equal(HttpStatusCode.NoContent,
            (await http.DeleteAsync("/api/settings/integrations/firecrawl")).StatusCode);

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/integrations"));
        Assert.Equal("none", Provider(doc.RootElement, "firecrawl").GetProperty("source").GetString());
    }
}
