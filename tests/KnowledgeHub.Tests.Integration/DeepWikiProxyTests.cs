using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-07: proxy tools listed when enabled, hidden when disabled, validation without upstream.
public class DeepWikiProxyEnabledTests : IClassFixture<DeepWikiProxyEnabledTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-dw-{Guid.NewGuid():N}.db"),
                    ["DeepWiki:Enabled"] = "true"
                }));
    }

    private readonly Fixture _factory;
    public DeepWikiProxyEnabledTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_IncludesUpstreamNamedTools()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var names = TestMcp.ToolNames(await mcp.SendAsync("tools/list"));

        Assert.Contains("ask_question", names);
        Assert.Contains("read_wiki_structure", names);
        Assert.Contains("read_wiki_contents", names);
    }

    [Fact]
    public async Task InvalidRepoName_InvalidParams_NoUpstreamCall()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new { name = "read_wiki_structure", arguments = new { repoName = "nope" } }));
        Assert.Contains("-32602", ex.Message);
    }
}

public class DeepWikiProxyDisabledTests : IClassFixture<DeepWikiProxyDisabledTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-dw-off-{Guid.NewGuid():N}.db"),
                    ["DeepWiki:Enabled"] = "false"
                }));
    }

    private readonly Fixture _factory;
    public DeepWikiProxyDisabledTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_HidesProxyTools()
    {
        var mcp = await TestMcp.ConnectAsync(_factory);
        var names = TestMcp.ToolNames(await mcp.SendAsync("tools/list"));

        Assert.DoesNotContain("ask_question", names);
        Assert.DoesNotContain("read_wiki_structure", names);
        Assert.DoesNotContain("read_wiki_contents", names);
        Assert.Contains("search_knowledge", names);
    }
}
