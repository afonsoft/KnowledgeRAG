using System.Text.Json;
using System.Threading.Channels;
using KnowledgeHub.McpEngine.Activity;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace KnowledgeHub.Tests.Unit.McpEngine;

// Covers SPEC-01 RF-003/RF-004: the tools/call request filter (latency,
// outcome, concurrency gate) and the incoming-message telemetry filter.
public sealed class McpActivityFiltersTests
{
    private sealed class FakeFeed : IMcpActivityFeed
    {
        public List<McpActivityEvent> Events { get; } = [];
        public int Capacity => 100;
        public void Record(McpActivityEvent e)
        {
            Events.Add(e);
            Published?.Invoke(e);
        }
        public IReadOnlyList<McpActivityEvent> Snapshot() => Events;
        public event Action<McpActivityEvent>? Published;
    }

    private sealed class FakeMetrics : IMcpRequestMetrics
    {
        public List<(string Method, string Mode, bool Succeeded)> Calls { get; } = [];
        public void Record(string method, string sessionMode, bool succeeded) =>
            Calls.Add((method, sessionMode, succeeded));
    }

    private sealed class FakeTransport(string? sessionId) : ITransport
    {
        public string? SessionId => sessionId;
        public ChannelReader<JsonRpcMessage> MessageReader { get; } =
            Channel.CreateUnbounded<JsonRpcMessage>().Reader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static McpServer NewServer(string? sessionId = "sess-1")
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test", Version = "1.0" }
        };
        return McpServer.Create(
            new FakeTransport(sessionId), options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Next(
        McpRequestHandler<CallToolRequestParams, CallToolResult> inner) => inner;

    private static RequestContext<CallToolRequestParams> ToolCallContext(McpServer server, string toolName) =>
        new(server,
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId(1) },
            new CallToolRequestParams { Name = toolName });

    [Fact]
    public async Task ToolCallFilter_Success_RecordsSucceededEvent()
    {
        var feed = new FakeFeed();
        var metrics = new FakeMetrics();
        var gate = new SessionCallGate();
        var server = NewServer();
        var filter = McpActivityFilters.CreateToolCallFilter(feed, gate, metrics);

        var handler = filter(Next((_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = "ok" }]
            })));

        var result = await handler(ToolCallContext(server, "search_knowledge"), default);

        Assert.False(result.IsError);
        var evt = Assert.Single(feed.Events);
        Assert.Equal(McpActivityKind.ToolCall, evt.Kind);
        Assert.Equal("tools/call", evt.Method);
        Assert.Equal("search_knowledge", evt.ToolName);
        Assert.True(evt.Succeeded);
        Assert.Null(evt.Error);
        Assert.Equal("sess-1", evt.SessionId);
        Assert.Equal(("tools/call", "stateful", true), metrics.Calls.Single());
    }

    [Fact]
    public async Task ToolCallFilter_IsErrorResult_RecordsFailureWithErrorText()
    {
        var feed = new FakeFeed();
        var gate = new SessionCallGate();
        var server = NewServer();
        var filter = McpActivityFilters.CreateToolCallFilter(feed, gate);

        var handler = filter(Next((_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "bad input" }]
            })));

        var result = await handler(ToolCallContext(server, "run_flow"), default);

        Assert.True(result.IsError);
        var evt = Assert.Single(feed.Events);
        Assert.False(evt.Succeeded);
        Assert.Equal("bad input", evt.Error);
    }

    [Fact]
    public async Task ToolCallFilter_Throw_RecordsErrorAndRethrows()
    {
        var feed = new FakeFeed();
        var gate = new SessionCallGate();
        var server = NewServer();
        var filter = McpActivityFilters.CreateToolCallFilter(feed, gate);

        var handler = filter(Next((_, _) =>
            throw new InvalidOperationException("handler blew up")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler(ToolCallContext(server, "x"), default));
        Assert.Equal("handler blew up", ex.Message);

        var evt = Assert.Single(feed.Events);
        Assert.False(evt.Succeeded);
        Assert.Equal("handler blew up", evt.Error);
        // gate released — a second acquire completes immediately
        await gate.AcquireAsync("sess-1", default);
    }

    [Fact]
    public async Task ToolCallFilter_ThrownMcpProtocolException_RecordsProtocolError()
    {
        var feed = new FakeFeed();
        var server = NewServer();
        var filter = McpActivityFilters.CreateToolCallFilter(feed, new SessionCallGate());

        var handler = filter(Next((_, _) =>
            throw new McpProtocolException("missing arg", McpErrorCode.InvalidParams)));

        await Assert.ThrowsAsync<McpProtocolException>(
            async () => await handler(ToolCallContext(server, "x"), default));
        var evt = Assert.Single(feed.Events);
        Assert.False(evt.Succeeded);
        Assert.Contains("missing arg", evt.Error);
    }

    [Fact]
    public async Task ToolCallFilter_StatelessSession_ReportsStatelessMetric()
    {
        var feed = new FakeFeed();
        var metrics = new FakeMetrics();
        var server = NewServer(sessionId: null);
        var filter = McpActivityFilters.CreateToolCallFilter(feed, new SessionCallGate(), metrics);

        var handler = filter(Next((_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult { IsError = false })));

        await handler(ToolCallContext(server, "t"), default);
        Assert.Equal(("tools/call", "stateless", true), metrics.Calls.Single());
    }

    [Fact]
    public async Task ToolCallFilter_GateSerializesConcurrentCallsPerSession()
    {
        var feed = new FakeFeed();
        var gate = new SessionCallGate(maxConcurrentPerSession: 1);
        var server = NewServer();
        var filter = McpActivityFilters.CreateToolCallFilter(feed, gate);

        var gateOpen = new TaskCompletionSource();
        var handler = filter(Next(async (_, _) =>
        {
            gateOpen.TrySetResult();
            await Task.Delay(75);
            return new CallToolResult { IsError = false };
        }));

        var first = handler(ToolCallContext(server, "a"), default).AsTask();
        await gateOpen.Task; // first call is inside the gate
        var second = handler(ToolCallContext(server, "b"), default).AsTask();
        Assert.False(second.IsCompleted); // blocked on the gate
        await Task.WhenAll(first, second);
        Assert.Equal(2, feed.Events.Count);
    }

    [Fact]
    public async Task RequestTelemetry_RecordsNonToolCallRequests()
    {
        var feed = new FakeFeed();
        var metrics = new FakeMetrics();
        var registry = new McpSessionRegistry();
        var server = NewServer();
        var filter = McpActivityFilters.CreateRequestTelemetryFilter(feed, registry, metrics);

        var handler = filter((ctx, _) => Task.CompletedTask);
        var context = new MessageContext(
            server, new JsonRpcRequest { Method = "tools/list", Id = new RequestId(1) });
        await handler(context, default);

        var evt = Assert.Single(feed.Events);
        Assert.Equal(McpActivityKind.Request, evt.Kind);
        Assert.Equal("tools/list", evt.Method);
        Assert.True(evt.Succeeded);
        Assert.Equal(("tools/list", "stateful", true), metrics.Calls.Single());
    }

    [Fact]
    public async Task RequestTelemetry_SkipsToolCalls_ButRegistersSession()
    {
        var feed = new FakeFeed();
        var registry = new McpSessionRegistry();
        var server = NewServer();
        var filter = McpActivityFilters.CreateRequestTelemetryFilter(feed, registry);

        var handler = filter((ctx, _) => Task.CompletedTask);
        var context = new MessageContext(
            server, new JsonRpcRequest { Method = "tools/call", Id = new RequestId(2) });
        await handler(context, default);

        Assert.Empty(feed.Events);
        Assert.Contains(registry.Active, s => ReferenceEquals(s, server));
    }

    [Fact]
    public async Task RequestTelemetry_PassesThroughNotifications()
    {
        var feed = new FakeFeed();
        var registry = new McpSessionRegistry();
        var server = NewServer();
        var filter = McpActivityFilters.CreateRequestTelemetryFilter(feed, registry);

        var handler = filter((ctx, _) => Task.CompletedTask);
        await handler(new MessageContext(server, new JsonRpcNotification { Method = "notifications/initialized" }), default);
        Assert.Empty(feed.Events); // notifications aren't JSON-RPC requests
    }

    [Fact]
    public async Task RequestTelemetry_RecordsFailureAndRethrows()
    {
        var feed = new FakeFeed();
        var metrics = new FakeMetrics();
        var registry = new McpSessionRegistry();
        var server = NewServer();
        var filter = McpActivityFilters.CreateRequestTelemetryFilter(feed, registry, metrics);

        var handler = filter((ctx, _) => throw new InvalidOperationException("init failed"));
        var context = new MessageContext(
            server, new JsonRpcRequest { Method = "initialize", Id = new RequestId(1) });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler(context, default));
        var evt = Assert.Single(feed.Events);
        Assert.False(evt.Succeeded);
        Assert.Equal("init failed", evt.Error);
        Assert.Equal(("initialize", "stateful", false), metrics.Calls.Single());
    }
}
