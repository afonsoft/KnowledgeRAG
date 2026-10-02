using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-playground-tools RF-002/RF-003/RF-006.
public class ToolsApiTests : IClassFixture<ToolsApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-toolsapi-{Guid.NewGuid():N}.db");
        public string Vault { get; } = Path.Join(Path.GetTempPath(), $"vault-toolsapi-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Vault);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "true"
                }));
        }
    }

    private readonly Fixture _factory;

    public ToolsApiTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_ReturnsCatalog_MatchingMcpSurface()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var list = await http.GetFromJsonAsync<ToolListResponse>("api/tools");
        var names = list!.Tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("search_knowledge", names);
        Assert.Contains("ask_knowledge", names);
        Assert.Contains("write_knowledge", names);
        Assert.Contains("ask_question", names);
        Assert.Contains("read_wiki_structure", names);
        Assert.Contains("read_wiki_contents", names);

        var search = list.Tools.First(t => t.Name == "search_knowledge");
        Assert.True(search.ReadOnly);
        Assert.Contains(search.InputSchema.GetProperty("required").EnumerateArray(),
            e => e.GetString() == "query");
    }

    [Fact]
    public async Task ToolsList_ReflectsSourceRegistration()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var name = $"ApiVault{Guid.NewGuid():N}";
        var create = await http.PostAsJsonAsync("/api/sources", new
        {
            name,
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault },
            isActive = true
        });
        create.EnsureSuccessStatusCode();

        var list = await http.GetFromJsonAsync<ToolListResponse>("api/tools");
        var names = list!.Tools.Select(t => t.Name).ToHashSet();
        Assert.Contains($"query_{name.ToLowerInvariant()}", names);
        Assert.Contains("read_document", names);
        Assert.Contains("write_note", names);
    }

    [Fact]
    public async Task ToolsCall_SearchKnowledge_ReturnsTextContent()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var response = await http.PostAsJsonAsync("api/tools/search_knowledge", new { query = "nada", topK = 2 });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("isError").GetBoolean());
        var content = doc.RootElement.GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.NotNull(content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_Is404()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var response = await http.PostAsJsonAsync("api/tools/ghost_tool", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredArg_IsIsErrorResult()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var response = await http.PostAsJsonAsync("api/tools/search_knowledge", new { });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
        Assert.Contains("query", doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
    }
}
