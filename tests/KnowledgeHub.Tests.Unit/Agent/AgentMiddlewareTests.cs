using System.Text.Json.Nodes;
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

namespace KnowledgeHub.Tests.Unit.Agent;

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-5: middleware ordering (first
/// registered = outermost), wrap_model_call once per iteration, wrap_tool_call
/// once per tool call, custom middleware through DI, Agent:Middleware ordering.
/// </summary>
public sealed class AgentMiddlewareTests
{
    [Fact]
    public async Task ModelCall_FirstRegisteredWrapsOutermost()
    {
        var order = new List<string>();
        var outer = new RecordingMiddleware("outer", order);
        var inner = new RecordingMiddleware("inner", order);
        var pipeline = new AgentMiddlewarePipeline([outer, inner]);

        var response = await pipeline.InvokeModelCallAsync(Ctx(),
            () => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))),
            CancellationToken.None);

        Assert.Equal("done", response.Text);
        // outer wraps inner wraps terminal: outer sees the call first,
        // inner's after-next runs before outer's after-next.
        Assert.Equal(
            ["outer:pre-model", "inner:pre-model", "inner:post-model", "outer:post-model"],
            order);
    }

    [Fact]
    public async Task ToolCall_FirstRegisteredWrapsOutermost()
    {
        var order = new List<string>();
        var pipeline = new AgentMiddlewarePipeline(
            [new RecordingMiddleware("outer", order), new RecordingMiddleware("inner", order)]);
        var call = new FunctionCallContent("c1", "tool_x", new Dictionary<string, object?>());

        var result = await pipeline.InvokeToolCallAsync(Ctx(), call,
            () => Task.FromResult<object?>("tool-result"), CancellationToken.None);

        Assert.Equal("tool-result", result);
        Assert.Equal(
            ["outer:pre-tool:tool_x", "inner:pre-tool:tool_x",
             "inner:post-tool:tool_x", "outer:post-tool:tool_x"],
            order);
    }

    [Fact]
    public async Task ConfiguredOrder_ReordersAndFilters_ByName()
    {
        var order = new List<string>();
        var pipeline = new AgentMiddlewarePipeline(
            [
                new RecordingMiddleware("alpha", order),
                new RecordingMiddleware("beta", order),
                new RecordingMiddleware("gamma", order),
            ],
            order: ["gamma", "alpha"]);

        await pipeline.BeforeAgentAsync(Ctx(), CancellationToken.None);

        Assert.Equal(["gamma:before", "alpha:before"], order);
        Assert.Equal(["gamma", "alpha"], pipeline.Middlewares.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void ConfiguredOrder_UnknownName_Throws()
    {
        var pipeline = () => new AgentMiddlewarePipeline(
            [new RecordingMiddleware("alpha", [])], order: ["nope"]);
        var ex = Assert.Throws<InvalidOperationException>(pipeline);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task AgentLoop_WrapModelCall_RunsOncePerIteration()
    {
        var counter = new CountingMiddleware();
        await RunAgentLoop(counter);

        Assert.Equal(2, counter.ModelCalls); // tool-call turn + final-answer turn
        Assert.Equal(1, counter.ToolCalls);  // a single echo_tool dispatch
        Assert.Equal(1, counter.BeforeCount);
        Assert.Equal(1, counter.AfterCount);
        Assert.Equal([1, 2], counter.ModelIterations);
    }

    [Fact]
    public async Task AgentLoop_ExternalMiddleware_IsOutermost_AroundOtherCalls()
    {
        var order = new List<string>();
        var outer = new RecordingMiddleware("outer", order);
        var inner = new RecordingMiddleware("inner", order);
        await RunAgentLoop(outer, inner);

        // For each model call the outer middleware must see the call before
        // inner and finish after it.
        Assert.Equal(
            ["outer:before", "inner:before",
             "outer:pre-model", "inner:pre-model", "inner:post-model", "outer:post-model",
             "outer:pre-tool:echo_tool", "inner:pre-tool:echo_tool",
             "inner:post-tool:echo_tool", "outer:post-tool:echo_tool",
             "outer:pre-model", "inner:pre-model", "inner:post-model", "outer:post-model",
             "outer:after", "inner:after"],
            order);
    }

    [Fact]
    public async Task AgentLoop_CustomMiddlewareViaDi_Executes()
    {
        var counter = new CountingMiddleware();
        var services = new ServiceCollection()
            .AddSingleton<IAgentMiddleware>(counter)
            .BuildServiceProvider();

        // Mirrors what KnowledgeHubServiceCollectionExtensions does: the
        // resolved IEnumerable<IAgentMiddleware> is handed to AgentService.
        var middlewares = services.GetServices<IAgentMiddleware>().ToList();
        await RunAgentLoop(middlewares.ToArray());

        Assert.Same(counter, Assert.Single(middlewares));
        Assert.True(counter.ModelCalls > 0);
        Assert.Equal(1, counter.BeforeCount);
    }

    private static async Task<AgentResponse> RunAgentLoop(params IAgentMiddleware[] middlewares)
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
        var services = new ServiceCollection().BuildServiceProvider();
        var agent = new AgentService(new TwoTurnChatClient(), services, new StubCatalog(tool), db,
            new AgentOptions(), new AgentDiagnostics(null, null, null),
            NullLogger<AgentService>.Instance,
            new KnowledgeHub.Server.Chat.ChatProviderOptions(),
            middlewares);

        return await agent.RunAsync(new AgentRequest { Prompt = "hi" });
    }

    private static AgentMiddlewareContext Ctx() => new()
    {
        Request = new AgentRequest { Prompt = "test" },
        Messages = [],
        Services = new ServiceCollection().BuildServiceProvider()
    };

    private sealed class RecordingMiddleware(string name, List<string> order) : IAgentMiddleware
    {
        public string Name => name;

        public ValueTask BeforeAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct)
        {
            order.Add($"{name}:before");
            return ValueTask.CompletedTask;
        }

        public async Task<ChatResponse> WrapModelCallAsync(
            AgentMiddlewareContext ctx, Func<Task<ChatResponse>> next, CancellationToken ct)
        {
            order.Add($"{name}:pre-model");
            var r = await next();
            order.Add($"{name}:post-model");
            return r;
        }

        public async Task<object?> WrapToolCallAsync(
            AgentMiddlewareContext ctx, FunctionCallContent call,
            Func<Task<object?>> next, CancellationToken ct)
        {
            order.Add($"{name}:pre-tool:{call.Name}");
            var r = await next();
            order.Add($"{name}:post-tool:{call.Name}");
            return r;
        }

        public ValueTask AfterAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct)
        {
            order.Add($"{name}:after");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingMiddleware : IAgentMiddleware
    {
        public int BeforeCount { get; private set; }
        public int AfterCount { get; private set; }
        public int ModelCalls { get; private set; }
        public int ToolCalls { get; private set; }
        public List<int> ModelIterations { get; } = [];

        public ValueTask BeforeAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct)
        {
            BeforeCount++;
            return ValueTask.CompletedTask;
        }

        public async Task<ChatResponse> WrapModelCallAsync(
            AgentMiddlewareContext ctx, Func<Task<ChatResponse>> next, CancellationToken ct)
        {
            ModelCalls++;
            ModelIterations.Add(ctx.Iteration);
            return await next();
        }

        public async Task<object?> WrapToolCallAsync(
            AgentMiddlewareContext ctx, FunctionCallContent call,
            Func<Task<object?>> next, CancellationToken ct)
        {
            ToolCalls++;
            return await next();
        }

        public ValueTask AfterAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct)
        {
            AfterCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>First call requests echo_tool; second answers with text.</summary>
    private sealed class TwoTurnChatClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "echo_tool", new Dictionary<string, object?>())])));
            }
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "final answer"))
            { ModelId = "stub" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
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
