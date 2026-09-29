using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260926-mcp-sdk-alignment ACs: tools/list advertises title/annotations/
/// outputSchema + private cache metadata; search_knowledge emits
/// structuredContent; gated write tools use the MRTR input_required round-trip
/// (stateless) or in-band elicitation (stateful); agent_chat is served via the
/// Tasks extension for task-capable clients.
/// </summary>
public class McpSdkAlignmentTests : IClassFixture<McpSdkAlignmentTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-sdk-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private static readonly object ElicitationCaps = new
    {
        elicitation = new { }
    };

    private static readonly object TasksCaps = new
    {
        extensions = new Dictionary<string, object>
        {
            ["io.modelcontextprotocol/tasks"] = new { }
        }
    };

    private readonly Fixture _factory;
    private readonly HttpClient _admin;

    public McpSdkAlignmentTests(Fixture factory)
    {
        _factory = factory;
        _admin = TestAuth.Login(factory);
    }

    [Fact]
    public async Task ToolsList_AdvertisesTitleAnnotationsAndCacheMetadata()
    {
        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var list = await mcp.SendAsync("tools/list");

        // RF-005: catalog is per-credential — private scope + TTL.
        Assert.Equal("private", list.GetProperty("cacheScope").GetString());
        Assert.True(list.GetProperty("ttlMs").GetInt64() > 0);

        var tools = list.GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);

        var write = tools["write_knowledge"];
        Assert.Equal("Write knowledge", write.GetProperty("title").GetString());
        Assert.True(write.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.False(write.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        var search = tools["search_knowledge"];
        Assert.True(search.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean());
        Assert.True(search.TryGetProperty("outputSchema", out var schema)
            && schema.GetProperty("properties").TryGetProperty("results", out _));
    }

    [Fact]
    public async Task SearchKnowledge_ReturnsStructuredContent()
    {
        var token = $"TOK{Guid.NewGuid():N}";
        await SeedSourceAsync(token);

        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "search_knowledge",
            arguments = new { query = token }
        });

        Assert.True(result.GetProperty("structuredContent")
            .GetProperty("results").GetArrayLength() > 0);
        // text content preserved for compatibility
        Assert.Contains(token, result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    // ---- RF-003: MRTR input_required (stateless 2026-07-28 era) ---------------

    [Fact]
    public async Task WriteTool_ElicitationClient_GetsInputRequired_AndAcceptExecutes()
    {
        await SeedSourceAsync($"TOK{Guid.NewGuid():N}");
        await using var mcp = await TestMcp2026.CreateAsync(_factory, ElicitationCaps);

        var first = await mcp.SendAsync("tools/call", "write_knowledge", new
        {
            name = "write_knowledge",
            arguments = new { title = "mrtr-note", content = "pending approval" }
        });

        // RF-003: gated write call → resultType input_required + elicitation + state
        Assert.Equal("input_required", first.GetProperty("resultType").GetString());
        var state = first.GetProperty("requestState").GetString();
        Assert.False(string.IsNullOrEmpty(state));
        Assert.Contains("elicitation", first.GetProperty("inputRequests")
            .GetProperty("approval").GetProperty("method").GetString());

        // Retry with an accepted elicitation → the stored call executes.
        var retry = await mcp.SendAsync("tools/call", "write_knowledge", new
        {
            name = "write_knowledge",
            requestState = state,
            inputResponses = new Dictionary<string, object>
            {
                ["approval"] = new { action = "accept", content = new { confirm = true } }
            }
        });
        Assert.True(!retry.TryGetProperty("isError", out var ie) || !ie.GetBoolean());
        Assert.Contains("mrtr-note",
            retry.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task WriteTool_ElicitationClient_DeclineReturnsInformativeError()
    {
        await using var mcp = await TestMcp2026.CreateAsync(_factory, ElicitationCaps);

        var first = await mcp.SendAsync("tools/call", "write_knowledge", new
        {
            name = "write_knowledge",
            arguments = new { title = "denied-note", content = "x" }
        });
        Assert.Equal("input_required", first.GetProperty("resultType").GetString());
        var state = first.GetProperty("requestState").GetString()!;

        var retry = await mcp.SendAsync("tools/call", "write_knowledge", new
        {
            name = "write_knowledge",
            requestState = state,
            inputResponses = new Dictionary<string, object>
            {
                ["approval"] = new { action = "decline" }
            }
        });
        Assert.True(retry.GetProperty("isError").GetBoolean());
        Assert.Contains("not approved",
            retry.GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>Stateful clients get the same approval as an in-band
    /// <c>elicitation/create</c> request — the SDK fulfills the MRTR round-trip
    /// on the session stream.</summary>
    [Fact]
    public async Task WriteTool_StatefulElicitationClient_InBandApprovalExecutes()
    {
        await SeedSourceAsync($"TOK{Guid.NewGuid():N}");
        await using var mcp = await TestMcp.ConnectAsync(_factory, ElicitationCaps);

        var sawElicitation = false;
        var result = await mcp.SendInteractiveAsync("tools/call", new
        {
            name = "write_knowledge",
            arguments = new { title = "inband-note", content = "hi" }
        }, (method, _) =>
        {
            if (method == "elicitation/create")
            {
                sawElicitation = true;
                return new { action = "accept", content = new { confirm = true } };
            }
            return null;
        });

        Assert.True(sawElicitation);
        Assert.Contains("inband-note",
            result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task WriteTool_PlainClient_ExecutesWithoutMrtrGate()
    {
        var token = $"TOK{Guid.NewGuid():N}";
        await SeedSourceAsync(token);

        // No elicitation capability → today's direct execution is preserved.
        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "write_knowledge",
            arguments = new { title = "direct-note", content = token }
        });
        Assert.True(!result.TryGetProperty("isError", out var ie) || !ie.GetBoolean());
        Assert.Contains("direct-note",
            result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    // ---- RF-004: tasks extension (stateless 2026-07-28 era) --------------------

    [Fact]
    public async Task AgentChat_TasksCapableClient_ReturnsTaskHandle()
    {
        await using var mcp = await TestMcp2026.CreateAsync(_factory, TasksCaps);

        var created = await mcp.SendAsync("tools/call", "agent_chat", new
        {
            name = "agent_chat",
            arguments = new { prompt = "hi" }
        });

        Assert.Equal("task", created.GetProperty("resultType").GetString());
        var taskId = created.GetProperty("taskId").GetString();
        Assert.False(string.IsNullOrEmpty(taskId));

        // Poll until terminal — no chat provider configured, so the run
        // resolves quickly (completed with isError content or failed).
        JsonElement status = default;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            status = await mcp.SendAsync("tasks/get", taskId!, new { taskId });
            var s = status.GetProperty("status").GetString();
            if (s is "completed" or "failed" or "cancelled")
                break;
        }
        Assert.Contains(status.GetProperty("status").GetString(),
            new[] { "completed", "failed", "cancelled" });
    }

    [Fact]
    public async Task AgentChat_PlainClient_StaysSynchronous()
    {
        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "agent_chat",
            arguments = new { prompt = "hi" }
        });
        // No tasks capability → synchronous isError (no chat provider configured).
        Assert.False(result.TryGetProperty("taskId", out _));
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    private async Task SeedSourceAsync(string token)
    {
        var dir = Path.Join(Path.GetTempPath(), $"sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Join(dir, "doc.txt"), $"contents about {token}");
        var response = await _admin.PostAsJsonAsync("/api/sources", new
        {
            name = $"sdk-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = dir },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        (await _admin.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null)).EnsureSuccessStatusCode();
    }
}
