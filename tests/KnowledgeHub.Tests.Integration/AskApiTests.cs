using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-llm-answer-synthesis ACs: POST /api/ask synthesis with
// citations, generate=false passthrough, no-provider behavior, MCP structuredContent.
public class AskApiTests : IClassFixture<AskApiTests.Fixture>, IClassFixture<AskApiTests.NoChatFixture>, IDisposable
{
    /// <summary>Host with a stub IChatClient — simulates a configured chat provider.</summary>
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-ask-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(new StubChatClient()));
        }
    }

    /// <summary>Host without a chat provider (Chat:Provider=none — the default).</summary>
    public sealed class NoChatFixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-asknone-{Guid.NewGuid():N}.db")
                }));
        }
    }

    private readonly HttpClient _client;
    private readonly Fixture _factory;
    private readonly NoChatFixture _noChat;
    private readonly string _dir;

    public AskApiTests(Fixture factory, NoChatFixture noChat)
    {
        _factory = factory;
        _noChat = noChat;
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"ask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) { _ = ex.Message; }
    }

    private async Task<Guid> SeedSource(string token)
    {
        var file = Path.Join(_dir, $"{token}.txt");
        await File.WriteAllTextAsync(file, $"knowledge about {token}");
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"ask-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        return source.Id;
    }

    [Fact]
    public async Task Ask_GeneratesAnswer_WithRealCitations()
    {
        var sourceId = await SeedSource($"ASKTOKEN{Guid.NewGuid():N}");

        var response = await _client.PostAsJsonAsync("/api/ask", new
        {
            question = "what is in the knowledge base?"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AskResponse>())!;
        Assert.True(result.Generated);
        Assert.Equal("Stubbed answer citing [1].", result.Answer);
        Assert.Equal("stub-model", result.Model);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(1, citation.Index);
        Assert.NotEmpty(citation.Uri);
        _ = sourceId;
    }

    [Fact]
    public async Task Ask_NoContext_DeclaresNoMatches()
    {
        var response = await _client.PostAsJsonAsync("/api/ask", new
        {
            question = "NOMATCHTOKEN-xyzzy anything?",
            mode = "lexical" // semantic always returns topK; lexical truly empty on no match
        });

        var result = (await response.Content.ReadFromJsonAsync<AskResponse>())!;
        Assert.True(result.Generated);
        Assert.Contains("no matching content", result.Answer);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public async Task Ask_GenerateFalse_ReturnsRawContext()
    {
        var token = $"RAWTOKEN{Guid.NewGuid():N}";
        await SeedSource(token);

        var response = await _client.PostAsJsonAsync("/api/ask", new
        {
            question = token,
            generate = false
        });

        var result = (await response.Content.ReadFromJsonAsync<AskResponse>())!;
        Assert.False(result.Generated);
        Assert.Null(result.Answer);
        Assert.NotNull(result.Context);
        Assert.NotEmpty(result.Context);
    }

    [Fact]
    public async Task Ask_McpTool_ReturnsStructuredContent()
    {
        var token = $"MCPTOKEN{Guid.NewGuid():N}";
        await SeedSource(token);

        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "ask_knowledge",
            arguments = new { question = token }
        });

        Assert.False(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Stubbed answer citing [1].", text);
        Assert.Contains("Citations:", text);

        var structured = result.GetProperty("structuredContent");
        Assert.True(structured.GetProperty("generated").GetBoolean());
        Assert.Equal("Stubbed answer citing [1].", structured.GetProperty("answer").GetString());
        Assert.Single(structured.GetProperty("citations").EnumerateArray());
    }

    [Fact]
    public async Task Ask_McpTool_VaultCitation_PathFeedsReadDocument()
    {
        // Covers SPEC-20260922-tool-descriptions-en-us RF-003/AC: citation.path is
        // the vault-relative path read_document accepts; text shows (path: …).
        var vault = Path.Join(_dir, $"vault{Guid.NewGuid():N}");
        Directory.CreateDirectory(vault);
        var token = $"VAULTTOKEN{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(Path.Join(vault, "note.md"), $"body about {token}");
        var name = $"v{Guid.NewGuid():N}"; // separator-free name slugifies to itself
        var src = await _client.PostAsJsonAsync("/api/sources", new
        {
            name,
            type = "ObsidianVault",
            configuration = new { path = vault },
            isActive = true
        });
        src.EnsureSuccessStatusCode();
        var source = (await src.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "ask_knowledge",
            arguments = new { question = token, mode = "lexical" }
        });

        Assert.False(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("(path: note.md)", text);

        var citation = result.GetProperty("structuredContent")
            .GetProperty("citations").EnumerateArray().First();
        Assert.Equal("note.md", citation.GetProperty("path").GetString());

        var read = await mcp.SendAsync("tools/call", new
        {
            name = "read_document",
            arguments = new { path = "note.md", source = name }
        });
        Assert.False(read.GetProperty("isError").GetBoolean());
        Assert.Contains(token, read.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Ask_McpTool_GenerateFalse_KeepsLegacyContext()
    {
        var token = $"LEGACYTOKEN{Guid.NewGuid():N}";
        await SeedSource(token);

        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "ask_knowledge",
            arguments = new { question = token, generate = false }
        });

        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Retrieved context", text);
        Assert.False(result.TryGetProperty("structuredContent", out _));
    }

    [Fact]
    public async Task Ask_NoProvider_GenerateTrue_ReturnsBadRequest()
    {
        var client = await TestAuth.LoginAsync(_noChat);
        var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "anything",
            generate = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ask_NoProvider_DefaultFallsBackToContext()
    {
        var client = await TestAuth.LoginAsync(_noChat);
        var file = Path.Join(_dir, "nochat.txt");
        await File.WriteAllTextAsync(file, "NOCHATTOKEN body");
        var src = await client.PostAsJsonAsync("/api/sources", new
        {
            name = $"nc-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir },
            isActive = true
        });
        var source = (await src.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        var response = await client.PostAsJsonAsync("/api/ask", new { question = "NOCHATTOKEN" });
        var result = (await response.Content.ReadFromJsonAsync<AskResponse>())!;
        Assert.False(result.Generated);
        Assert.Null(result.Answer);
        Assert.NotEmpty(result.Context!);
    }

    /// <summary>Always returns a fixed cited answer — stands in for a configured provider.</summary>
    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Stubbed answer citing [1]."))
            { ModelId = "stub-model" });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
