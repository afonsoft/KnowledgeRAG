using System.Net.Http.Json;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using static KnowledgeHub.Tests.Integration.TestMcp;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-04 ACs: dynamic tools/list, tools/call execution, catalog reactivity.
public class McpToolsTests : IClassFixture<McpToolsTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-tools-{Guid.NewGuid():N}.db");
        public string Vault { get; } = Path.Join(Path.GetTempPath(), $"vault-tools-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Vault);
            File.WriteAllText(Path.Join(Vault, "nota.md"),
                "# Nota de Teste\n\nconteúdo sobre embeddings vetores e busca semântica profunda");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "false"
                }));
        }
    }

    private readonly Fixture _factory;

    public McpToolsTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_AlwaysExposes_CoreTools()
    {
        var mcp = await ConnectAsync(_factory);
        var names = ToolNames(await mcp.SendAsync("tools/list"));

        Assert.Contains("search_knowledge", names);
        Assert.Contains("ask_knowledge", names);
        Assert.Contains("write_knowledge", names);
    }

    [Fact]
    public async Task ToolsList_ReflectsSourceActivation()
    {
        var mcp = await ConnectAsync(_factory);
        var http = await TestAuth.LoginAsync(_factory);

        var name = $"XVault{Guid.NewGuid():N}";
        var create = await http.PostAsJsonAsync("/api/sources", new
        {
            name,
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault },
            isActive = true
        });
        create.EnsureSuccessStatusCode();
        var source = (await create.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;

        var after = ToolNames(await mcp.SendAsync("tools/list"));
        Assert.Contains($"query_{name.ToLowerInvariant()}", after);
        Assert.Contains("read_document", after);
        Assert.Contains("write_note", after);

        await http.PostAsync($"/api/sources/{source.Id}/deactivate", null);
        var deactivated = ToolNames(await mcp.SendAsync("tools/list"));
        Assert.DoesNotContain($"query_{name.ToLowerInvariant()}", deactivated);
    }

    [Fact]
    public async Task ToolsCall_SearchKnowledge_ReturnsRankedHits()
    {
        var mcp = await ConnectAsync(_factory);
        var http = await TestAuth.LoginAsync(_factory);

        var create = await http.PostAsJsonAsync("/api/sources", new
        {
            name = $"vault-{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault }
        });
        var source = (await create.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await http.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        var result = await mcp.SendAsync("tools/call", new
        {
            name = "search_knowledge",
            arguments = new { query = "embeddings vetores", topK = 3 }
        });

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("Nota de Teste", text);
        Assert.False(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsProtocolError()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new { name = "nonexistent_tool", arguments = new { } }));
        Assert.Contains("-32601", ex.Message);
    }

    [Fact]
    public async Task ToolsCall_MissingRequiredArg_IsInvalidParams()
    {
        // SPEC-04: invalid params → McpProtocolException(InvalidParams) → JSON-RPC -32602
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new { name = "search_knowledge", arguments = new { } }));
        Assert.Contains("-32602", ex.Message);
        Assert.Contains("query", ex.Message);
    }

    [Fact]
    public async Task ToolsCall_ReadDocument_TraversalRejected()
    {
        var mcp = await ConnectAsync(_factory);
        var http = await TestAuth.LoginAsync(_factory);
        await http.PostAsJsonAsync("/api/sources", new
        {
            name = $"v-{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault }
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "read_document",
                arguments = new { path = "../outside.md" }
            }));
        Assert.Contains("-32602", ex.Message);
    }

    [Fact]
    public async Task ToolsCall_ReadOnlyVault_WritesRejected()
    {
        // SPEC-20260915-sources-edit-dialog RF-002: write tools refuse read-only sources.
        var mcp = await ConnectAsync(_factory);
        var http = await TestAuth.LoginAsync(_factory);

        var name = $"rovault{Guid.NewGuid():N}";
        var create = await http.PostAsJsonAsync("/api/sources", new
        {
            name,
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault, readOnly = true }
        });
        create.EnsureSuccessStatusCode();
        var slug = ToolSlugger.Slugify(name);

        var noteResult = await mcp.SendAsync("tools/call", new
        {
            name = "write_note",
            arguments = new { path = "blocked-note", content = "nope", source = slug }
        });
        Assert.True(noteResult.GetProperty("isError").GetBoolean());
        Assert.Contains("read-only",
            noteResult.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(File.Exists(Path.Join(_factory.Vault, "blocked-note.md")));

        var kwResult = await mcp.SendAsync("tools/call", new
        {
            name = "write_knowledge",
            arguments = new { title = "Blocked", content = "nope", source = slug }
        });
        Assert.True(kwResult.GetProperty("isError").GetBoolean());
        Assert.Contains("read-only",
            kwResult.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(File.Exists(Path.Join(_factory.Vault, "Blocked.md")));
    }

    [Fact]
    public async Task ResourcesList_AndRead_Catalog()
    {
        var mcp = await ConnectAsync(_factory);
        var list = await mcp.SendAsync("resources/list");
        var uris = list.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("uri").GetString()!).ToList();
        Assert.Contains("knowledge://sources", uris);

        var read = await mcp.SendAsync("resources/read", new { uri = "knowledge://sources" });
        var text = read.GetProperty("contents")[0].GetProperty("text").GetString();
        Assert.Contains("\"Name\"", text);
    }
}
