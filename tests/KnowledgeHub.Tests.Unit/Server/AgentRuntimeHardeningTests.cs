using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KnowledgeHub.Server.Agent;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// SPEC-20260923-agent-runtime-hardening: bounded SSE channel ceiling (RF-001),
/// ChatOptions reuse across iterations (RF-002), HTTP resilience retry (RF-004).
/// </summary>
public sealed class AgentRuntimeHardeningTests
{
    [Fact]
    public async Task BoundedChannel_WriterBlocksAtCapacity_NothingDropped()
    {
        var channel = AgentService.CreateEventChannel(4);
        for (var i = 0; i < 4; i++)
            await channel.Writer.WriteAsync(new SseEvent("token", new { i }));

        var extra = channel.Writer.WriteAsync(new SseEvent("token", new { i = 4 })).AsTask();
        await Task.Delay(100);
        Assert.False(extra.IsCompleted); // Wait mode — producer blocks, never drops

        await channel.Reader.ReadAsync();
        await extra; // unblocks after a read
        Assert.Equal(4, channel.Reader.Count);
    }

    [Fact]
    public async Task AgentLoop_ReusesSameChatOptionsInstanceAcrossIterations()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var tool = new CatalogTool
        {
            Name = "echo_tool",
            Description = "echo",
            InputSchema = new JsonObject { ["type"] = "object" },
            ReadOnly = true,
            Handler = (_, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "ok" }]
            })
        };
        var catalog = new StubCatalog(tool);
        var chat = new CapturingChatClient();
        var services = new ServiceCollection().BuildServiceProvider();
        var agent = new AgentService(chat, services, catalog, db, new AgentOptions(),
            new AgentDiagnostics(null, null, null), NullLogger<AgentService>.Instance);

        var response = await agent.RunAsync(new AgentRequest { Prompt = "hi" });

        Assert.Equal("final answer", response.Answer);
        Assert.Equal(2, chat.CapturedOptions.Count);
        Assert.Same(chat.CapturedOptions[0], chat.CapturedOptions[1]);
    }

    [Fact]
    public async Task ResilienceHandler_RetriesTransientFailure()
    {
        var handler = new FailOnceHandler();
        var services = new ServiceCollection()
            .AddHttpClient("embeddings")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler()
            .Services
            .BuildServiceProvider();

        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("embeddings");
        var response = await client.GetAsync("http://localhost/embed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    private sealed class FailOnceHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(
                Attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }

    /// <summary>First call requests the echo tool; second answers with text.</summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatOptions?> CapturedOptions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedOptions.Add(options);
            if (CapturedOptions.Count == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "echo_tool", new Dictionary<string, object?>())])));
            }
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "final answer"))
            { ModelId = "stub" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubCatalog(CatalogTool tool) : IDynamicToolCatalog
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>([tool]);
        public Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>([tool]);
    }
}
