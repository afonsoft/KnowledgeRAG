using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static KnowledgeHub.Tests.Integration.TestMcp;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20261001-mcp-recall-ergonomics: per-call budget, score floors,
// temporal window and the maxTokens response budget over the MCP surface —
// argument validation, schema exposure (pinned in McpContractTests) and the
// truncatedByTokens / abstention signals.
public class McpRecallErgonomicsTests : IClassFixture<McpRecallErgonomicsTests.Fixture>
{
    /// <summary>Host with a stub IChatClient — ask_knowledge can synthesize.</summary>
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), DbFileName());
        public string Vault { get; } = Path.Join(Path.GetTempPath(), VaultDirName());

        private static string DbFileName() => $"kh-ergo-{Guid.NewGuid():N}.db";
        private static string VaultDirName() => $"vault-ergo-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Vault);
            // Six notes sharing a common token, each ~700 chars — large enough
            // to force a truncation delta under the minimum maxTokens clamp.
            // Varied tokens: a single repeated char trips the EncodedPayload
            // content-scan flag and the chunks get excluded from retrieval.
            var bodies = Enumerable.Range(0, 6).Select(i =>
                "shareword " + string.Join(' ',
                    Enumerable.Range(0, 110).Select(j => $"doc{i}term{j}")));
            var i2 = 0;
            foreach (var body in bodies)
            {
                var note = $"ergo{i2++}.md";
                File.WriteAllText(Path.Join(Vault, note), $"# Ergo{i2}\n\n{body}");
            }
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "false"
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(new StubChatClient()));
        }
    }

    private readonly Fixture _factory;

    public McpRecallErgonomicsTests(Fixture factory) => _factory = factory;

    private async Task SeedVaultSource()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var name = $"ergov{Guid.NewGuid():N}";
        var create = await http.PostAsJsonAsync("/api/sources", new
        {
            name,
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault },
            isActive = true
        });
        create.EnsureSuccessStatusCode();
        var source = (await create.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        var sync = await http.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        var body = await sync.Content.ReadAsStringAsync();
        Assert.True(sync.IsSuccessStatusCode, $"sync failed: {sync.StatusCode} {body}");
        var result = System.Text.Json.JsonDocument.Parse(body).RootElement;
        Assert.True(result.GetProperty("chunksCreated").GetInt32() >= 6,
            $"vault sync should index the seeded notes — got: {body}");
    }

    [Fact]
    public async Task Search_InvalidBudget_IsInvalidParams()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "search_knowledge",
                arguments = new { query = "q", budget = "bogus" }
            }));
        Assert.Contains("-32602", ex.Message);
        Assert.Contains("budget", ex.Message);
    }

    [Fact]
    public async Task Search_MinScoresNotObject_IsInvalidParams()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "search_knowledge",
                arguments = new { query = "q", minScores = "high" }
            }));
        Assert.Contains("-32602", ex.Message);
        Assert.Contains("minScores", ex.Message);
    }

    [Fact]
    public async Task Search_MinScoresOutOfRange_IsInvalidParams()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "search_knowledge",
                arguments = new { query = "q", minScores = new { final = 1.5 } }
            }));
        Assert.Contains("-32602", ex.Message);
        Assert.Contains("minScores", ex.Message);
    }

    [Fact]
    public async Task Search_ReversedTemporalWindow_IsInvalidParams()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "search_knowledge",
                arguments = new
                {
                    query = "q",
                    temporalWindow = new { start = "2026-10-01", end = "2026-01-01" }
                }
            }));
        Assert.Contains("-32602", ex.Message);
        Assert.Contains("temporalWindow", ex.Message);
    }

    [Fact]
    public async Task Search_NewArgs_Accepted_AndUnknownArgsTolerated()
    {
        var mcp = await ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "search_knowledge",
            arguments = new
            {
                query = "shareword",
                budget = "low",
                minScores = new { semantic = 0.0, lexical = 0.0, final = 0.0 },
                temporalWindow = new { start = "2020-01-01", end = "2030-01-01" },
                maxTokens = 4096,
                somethingUnknown = 42
            }
        });
        Assert.False(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Search_MaxTokens_Truncates_AndFlags()
    {
        await SeedVaultSource();
        var mcp = await ConnectAsync(_factory);

        var unbounded = await mcp.SendAsync("tools/call", new
        {
            name = "search_knowledge",
            arguments = new { query = "shareword", topK = 50, limitMode = "fixed" }
        });
        var bounded = await mcp.SendAsync("tools/call", new
        {
            name = "search_knowledge",
            arguments = new { query = "shareword", topK = 50, limitMode = "fixed", maxTokens = 256 }
        });

        var unboundedCount = unbounded.GetProperty("structuredContent")
            .GetProperty("results").GetArrayLength();
        var boundedStructured = bounded.GetProperty("structuredContent");
        var boundedCount = boundedStructured.GetProperty("results").GetArrayLength();

        Assert.True(unboundedCount > boundedCount,
            $"expected truncation: unbounded={unboundedCount} bounded={boundedCount}");
        Assert.True(boundedStructured.GetProperty("truncatedByTokens").GetBoolean());
        Assert.Contains("truncated", bounded.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Ask_FinalFloor_Empty_Abstains_WithoutSynthesis()
    {
        // RF-003: a final floor that empties the result set makes ask_knowledge
        // abstain via the corrective-RAG path — the chat provider is never
        // called, even though evidence exists below the floor.
        await SeedVaultSource();
        var mcp = await ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "ask_knowledge",
            arguments = new
            {
                question = "shareword?",
                minScores = new { final = 0.999 } // fused/cosine scores never reach this
            }
        });

        Assert.False(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("sufficient evidence", text);

        var structured = result.GetProperty("structuredContent");
        Assert.False(structured.GetProperty("generated").GetBoolean());
        Assert.True(structured.GetProperty("insufficientEvidence").GetBoolean());
        Assert.Equal("insufficient", structured.GetProperty("retrievalGrade").GetString());
    }

    [Fact]
    public async Task Ask_NoFloor_SameCorpus_StillSynthesizes()
    {
        // Contrast: without the floor the same corpus synthesizes — the
        // abstention above is caused by the floor itself, not by missing data.
        await SeedVaultSource();
        var mcp = await ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "ask_knowledge",
            arguments = new { question = "shareword?" }
        });

        Assert.False(result.GetProperty("isError").GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.True(structured.GetProperty("generated").GetBoolean());
        Assert.Equal("Stubbed answer citing [1].", structured.GetProperty("answer").GetString());
        Assert.False(structured.GetProperty("insufficientEvidence").GetBoolean());
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
