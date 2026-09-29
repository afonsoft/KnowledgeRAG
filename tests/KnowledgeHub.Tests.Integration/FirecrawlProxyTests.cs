using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260916-firecrawl-mcp-proxy: static core tools listed when enabled,
// hidden when disabled, friendly no-key error, static fallback when the
// upstream tools/list fetch fails.
public class FirecrawlProxyEnabledTests : IClassFixture<FirecrawlProxyEnabledTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"kh-fc-{Guid.NewGuid():N}.db"),
                    ["Firecrawl:Enabled"] = "true",
                    ["Security:Egress:AllowPrivateNetworks"] = "true" // local fake API targets
                }));
    }

    private readonly Fixture _factory;
    public FirecrawlProxyEnabledTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_IncludesStaticCoreTools()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var names = TestMcp.ToolNames(await mcp.SendAsync("tools/list"));

        Assert.Contains("firecrawl_scrape", names);
        Assert.Contains("firecrawl_search", names);
        Assert.Contains("firecrawl_map", names);
        Assert.Contains("firecrawl_crawl", names);
        Assert.Contains("firecrawl_check_crawl_status", names);
        Assert.Contains("firecrawl_parse", names);
        Assert.Contains("search_knowledge", names);
    }

    [Fact]
    public async Task ToolCall_WithoutKey_FriendlyIsError()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "firecrawl_scrape",
            arguments = new { url = "https://example.com" }
        });

        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("Firecrawl API key", text);
    }
}

public class FirecrawlProxyDisabledTests : IClassFixture<FirecrawlProxyDisabledTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"kh-fc-off-{Guid.NewGuid():N}.db"),
                    ["Firecrawl:Enabled"] = "false"
                }));
    }

    private readonly Fixture _factory;
    public FirecrawlProxyDisabledTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_HidesFirecrawlTools()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var names = TestMcp.ToolNames(await mcp.SendAsync("tools/list"));

        Assert.DoesNotContain(names, n => n.StartsWith("firecrawl_", StringComparison.Ordinal));
        Assert.Contains("search_knowledge", names);
    }
}

// Key configured but upstream unreachable → dynamic merge fails → static core
// still served (last-known-good empty ⇒ static-only).
public class FirecrawlProxyFallbackTests : IClassFixture<FirecrawlProxyFallbackTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"kh-fc-fb-{Guid.NewGuid():N}.db"),
                    ["Firecrawl:Enabled"] = "true",
                    ["Firecrawl:ApiKey"] = "fc-test-unreachable",
                    ["Firecrawl:Endpoint"] = "http://127.0.0.1:1/mcp",
                    ["Firecrawl:TimeoutSeconds"] = "5"
                }));
    }

    private readonly Fixture _factory;
    public FirecrawlProxyFallbackTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_UpstreamDown_StaticCoreStillServed()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var names = TestMcp.ToolNames(await mcp.SendAsync("tools/list"));

        Assert.Contains("firecrawl_scrape", names);
        Assert.Contains("firecrawl_search", names);
    }

    [Fact]
    public async Task ToolCall_UpstreamDown_IsErrorNotCrash()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "firecrawl_scrape",
            arguments = new { url = "https://example.com" }
        });

        Assert.True(result.GetProperty("isError").GetBoolean());
    }
}
