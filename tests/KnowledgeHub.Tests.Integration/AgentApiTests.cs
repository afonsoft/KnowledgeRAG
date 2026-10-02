using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-agent-chat-loop ACs: multi-step loop, allowlist/allowWrite
// gating, iteration cap, structured result, no-provider error.
public class AgentApiTests : IClassFixture<AgentApiTests.Fixture>, IClassFixture<AskApiTests.NoChatFixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-agent-{Guid.NewGuid():N}.db");
        public ScriptedChatClient Chat { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(Chat));
        }
    }

    /// <summary>
    /// Scripted loop driver: first round (no tool results in history) asks for
    /// search_knowledge; after a FunctionResultContent exists, returns the final
    /// answer. A prompt containing "LOOP_FOREVER" keeps calling tools (cap test).
    /// </summary>
    public sealed class ScriptedChatClient : IChatClient
    {
        public List<List<string>> ToolsSeen { get; } = [];

        /// <summary>"role: text" for every message seen per call — lets tests assert context carry-over.</summary>
        public List<List<string>> MessagesSeen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var toolNames = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            ToolsSeen.Add(toolNames);
            MessagesSeen.Add(messages.Select(m => $"{m.Role.Value}:{m.Text}").ToList());

            // Plain-text answer when no tools are offered (e.g. thread summarization calls).
            if (toolNames.Count == 0)
            {
                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "summary text"))
                { ModelId = "scripted" });
            }

            var loopForever = messages.Any(m => m.Role == ChatRole.User && (m.Text ?? "").Contains("LOOP_FOREVER"));
            var wantsWrite = messages.Any(m => m.Role == ChatRole.User && (m.Text ?? "").Contains("WRITE_TEST"));
            var hasToolResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();

            if (!loopForever && hasToolResult)
            {
                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "agent final answer"))
                { ModelId = "scripted" });
            }

            // WRITE_TEST: exercise a mutating tool end-to-end when it is exposed.
            var (tool, args) = wantsWrite && toolNames.Contains("write_knowledge")
                ? ("write_knowledge", new Dictionary<string, object?>
                { ["title"] = "agent note", ["content"] = "written by agent" })
                : ("search_knowledge", new Dictionary<string, object?> { ["query"] = "x" });

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{ToolsSeen.Count}", tool, args)])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private readonly Fixture _factory;
    private readonly HttpClient _client;
    private readonly string _dir;

    public AgentApiTests(Fixture factory, AskApiTests.NoChatFixture noChat)
    {
        _factory = factory;
        _noChat = noChat;
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    private readonly AskApiTests.NoChatFixture _noChat;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) { _ = ex.Message; }
    }

    [Fact]
    public async Task Agent_TwoStepLoop_ReturnsAnswerAndSteps()
    {
        var response = await _client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "search then answer"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.Equal("agent final answer", result.Answer);
        Assert.Equal(2, result.Iterations);
        Assert.False(result.LimitReached);
        var step = Assert.Single(result.Steps);
        Assert.Equal("search_knowledge", step.Tool);
        Assert.Equal(1, step.Iteration);
        Assert.False(step.IsError);
        Assert.Contains("search_knowledge", result.ToolCalls);
    }

    [Fact]
    public async Task Agent_WithoutAllowWrite_HidesMutatingTools()
    {
        var before = _factory.Chat.ToolsSeen.Count;
        await _client.PostAsJsonAsync("/api/agent", new { prompt = "check tools" });

        var tools = _factory.Chat.ToolsSeen[^1];
        Assert.Contains("search_knowledge", tools);
        Assert.DoesNotContain("write_knowledge", tools);
        Assert.DoesNotContain("write_note", tools);
        _ = before;
    }

    [Fact]
    public async Task Agent_WithAllowWrite_ExposesWriteTools()
    {
        await _client.PostAsJsonAsync("/api/agent", new { prompt = "check tools", allowWrite = true });

        var tools = _factory.Chat.ToolsSeen[^1];
        Assert.Contains("write_knowledge", tools);
    }

    [Fact]
    public async Task Agent_AllowWrite_MutatingToolIsGatedByApproval()
    {
        // With the HITL gate (Agent:RequireApprovalFor="*"), allowWrite exposes the
        // tool but the call suspends into a pending approval instead of executing.
        var response = await _client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "WRITE_TEST",
            allowWrite = true
        });

        var result = (await response.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.NotNull(result.AwaitingApprovalId);
        Assert.Equal("write_knowledge", result.PendingTool);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task Agent_WriteTestWithoutAllowWrite_FallsBackToReadOnly()
    {
        var response = await _client.PostAsJsonAsync("/api/agent", new { prompt = "WRITE_TEST" });

        var result = (await response.Content.ReadFromJsonAsync<AgentResponse>())!;
        var step = Assert.Single(result.Steps);
        Assert.Equal("search_knowledge", step.Tool); // write_knowledge was never offered
    }

    [Fact]
    public async Task Agent_ToolAllowlist_RestrictsSurface()
    {
        await _client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "check tools",
            tools = new[] { "search_knowledge" }
        });

        var tools = _factory.Chat.ToolsSeen[^1];
        Assert.Equal(["search_knowledge"], tools);
    }

    [Fact]
    public async Task Agent_IterationCap_StopsGracefully()
    {
        var response = await _client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "LOOP_FOREVER",
            maxIterations = 2
        });

        var result = (await response.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.True(result.LimitReached);
        Assert.Equal(2, result.Iterations);
        Assert.Contains("iteration limit reached", result.Answer);
    }

    [Fact]
    public async Task Agent_NoProvider_RestBadRequest_AndMcpIsError()
    {
        var client = await TestAuth.LoginAsync(_noChat);
        var response = await client.PostAsJsonAsync("/api/agent", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var mcp = await TestMcp.ConnectAsync(_noChat);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "agent_chat",
            arguments = new { prompt = "x" }
        });
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Chat:Provider",
            result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Agent_McpTool_ReturnsStructuredResult()
    {
        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "agent_chat",
            arguments = new { prompt = "search then answer" }
        });

        Assert.False(result.GetProperty("isError").GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.Equal("agent final answer", structured.GetProperty("answer").GetString());
        Assert.Equal(2, structured.GetProperty("iterations").GetInt32());
        Assert.Single(structured.GetProperty("steps").EnumerateArray());
    }
}
