using System.Text.Json;
using A2A;
using KnowledgeHub.Server.A2A;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using A2ARole = A2A.Role;

namespace KnowledgeHub.Tests.Unit.A2a;

/// <summary>
/// SPEC-20261001-a2a-task-durability + SPEC-20260929-a2a-server-interop:
/// <see cref="KnowledgeHubA2AAgent"/> routing, scope gates, progress plumbing
/// (RF-002) and origin context stamping (RF-004) over a stub catalog.
/// </summary>
public sealed class A2aAgentTests
{
    private sealed class StubCatalog(IReadOnlyList<CatalogTool> tools) : IDynamicToolCatalog
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
            IServiceProvider services, CancellationToken ct) => Task.FromResult(tools);
        public Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(
            IServiceProvider services, CancellationToken ct) => Task.FromResult(tools);
    }

    private sealed class StubScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private static CatalogTool Tool(string name, bool readOnly = true,
        Func<ToolCallContext, CancellationToken, ValueTask<CallToolResult>>? handler = null) => new()
    {
        Name = name,
        Description = "stub",
        InputSchema = new System.Text.Json.Nodes.JsonObject(),
        ReadOnly = readOnly,
        Handler = handler ?? ((ctx, ct) => ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"ok-{name}" }]
        }))
    };

    private static Dictionary<string, JsonElement> Meta(string skill, string? agentName = null)
    {
        var json = agentName is null
            ? $"{{\"skill\":\"{skill}\"}}"
            : $"{{\"skill\":\"{skill}\",\"agentName\":\"{agentName}\"}}";
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private static RequestContext Context(
        string? taskId = null, string skill = "search_knowledge",
        string? agentName = null, string text = "go") => new()
    {
        TaskId = taskId ?? string.Empty,
        ContextId = "ctx-1",
        StreamingResponse = false,
        Message = new Message
        {
            Role = A2ARole.User,
            MessageId = "m1",
            Parts = [Part.FromText(text)],
            Metadata = Meta(skill, agentName)
        }
    };

    private static (KnowledgeHubA2AAgent Agent, ServiceProvider Provider, IServiceScope Scope)
        Build(IReadOnlyList<CatalogTool> tools, CallerScope? scope = null,
            bool withOrigin = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDynamicToolCatalog>(new StubCatalog(tools));
        if (scope is not null)
            services.AddSingleton<ICallerScopeProvider>(new StubScope(scope));
        if (withOrigin)
            services.AddScoped<WriteOriginContext>();
        var provider = services.BuildServiceProvider();
        var requestScope = provider.CreateScope();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            { RequestServices = requestScope.ServiceProvider }
        };
        return (new KnowledgeHubA2AAgent(accessor), provider, requestScope);
    }

    /// <summary>Reads queued events without hanging — the agent doesn't close
    /// the queue, the SDK's request handler does.</summary>
    private static async Task<List<StreamResponse>> DrainAsync(
        AgentEventQueue queue, int maxEvents = 16)
    {
        var events = new List<StreamResponse>();
        // NB: the SDK enumerator's DisposeAsync throws NotSupportedException —
        // don't `await using` it.
        var e = queue.GetAsyncEnumerator();
        while (events.Count < maxEvents)
        {
            StreamResponse next;
            try
            {
                var moved = await e.MoveNextAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(3));
                if (!moved) break;
                next = e.Current;
            }
            catch (TimeoutException) { break; }
            events.Add(next);
        }
        return events;
    }

    [Fact]
    public async Task MessagePath_ExecutesSkill_AndReplies()
    {
        var (agent, provider, scope) = Build([Tool("search_knowledge")]);
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(Context(), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        var reply = Assert.Single(events, e => e.Message is not null);
        Assert.Contains("ok-search_knowledge",
            reply.Message!.Parts.Where(p => p.Text is not null).Select(p => p.Text).First());
        scope.Dispose();
    }

    [Fact]
    public async Task MessagePath_UndelegableSkill_Rejected()
    {
        var (agent, provider, scope) = Build([]);
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(Context(skill: "delete_everything"), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        var reply = Assert.Single(events, e => e.Message is not null);
        Assert.Contains("not delegable",
            reply.Message!.Parts.Where(p => p.Text is not null).Select(p => p.Text).First());
        scope.Dispose();
    }

    [Fact]
    public async Task TaskPath_WriteTool_RequiresWriteScope()
    {
        var (agent, provider, scope) = Build(
            [Tool("write_knowledge", readOnly: false)],
            scope: new CallerScope(null, null, null, AllowWrite: false));
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(
            Context(taskId: "t1", skill: "write_knowledge"), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        var failed = events.Select(e => e.StatusUpdate)
            .Where(u => u is not null).LastOrDefault();
        Assert.NotNull(failed);
        Assert.Equal(TaskState.Failed, failed!.Status.State);
        scope.Dispose();
    }

    [Fact]
    public async Task TaskPath_Executes_AndCompletes()
    {
        var (agent, provider, scope) = Build([Tool("search_knowledge")]);
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(Context(taskId: "t1"), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        var states = events.Select(e => e.StatusUpdate?.Status.State)
            .Where(s => s is not null).ToList();
        Assert.Contains(TaskState.Working, states);
        Assert.Equal(TaskState.Completed, states.Last());
        Assert.Contains(events, e => e.ArtifactUpdate is not null);
        scope.Dispose();
    }

    [Fact]
    public async Task TaskPath_ToolProgress_EmitsWorkingUpdates()
    {
        var tool = Tool("search_knowledge", handler: async (ctx, ct) =>
        {
            if (ctx.OnProgress is { } report)
                await report("iteration 1/10 — reasoning", ct);
            return new CallToolResult
            { Content = [new TextContentBlock { Text = "ok-search_knowledge" }] };
        });
        var (agent, provider, scope) = Build([tool]);
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(Context(taskId: "t1"), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        var working = events
            .Where(e => e.StatusUpdate?.Status.State == TaskState.Working).ToList();
        Assert.True(working.Count >= 2,
            $"expected ≥2 working events, got {working.Count}");
        Assert.Contains(working, e => e.StatusUpdate!.Status.Message?.Parts
            .Any(p => p.Text is not null && p.Text.Contains("iteration 1")) == true);
        scope.Dispose();
    }

    [Fact]
    public async Task TaskPath_SlowSkill_HeartbeatEmitsWorking()
    {
        // >2s handler → the heartbeat fires a working update while it runs.
        var tool = Tool("search_knowledge", handler: async (ctx, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2600), ct);
            return new CallToolResult
            { Content = [new TextContentBlock { Text = "ok-slow" }] };
        });
        var (agent, provider, scope) = Build([tool]);
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(Context(taskId: "t1"), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        Assert.Contains(events, e => e.StatusUpdate?.Status.Message?.Parts
            .Any(p => p.Text is not null && p.Text.Contains("working — search_knowledge")) == true);
        scope.Dispose();
    }

    [Fact]
    public async Task Cancel_MarksTaskCanceled()
    {
        var (agent, provider, scope) = Build([]);
        await using var _ = provider;
        var queue = new AgentEventQueue();

        await agent.CancelAsync(Context(taskId: "t1"), queue, CancellationToken.None);
        var events = await DrainAsync(queue);

        Assert.Contains(events, e =>
            e.StatusUpdate?.Status.State == TaskState.Canceled);
        scope.Dispose();
    }

    [Fact]
    public async Task TaskPath_SetsWriteOrigin_FromMetadata()
    {
        var (agent, provider, scope) = Build([Tool("write_knowledge", readOnly: false)],
            withOrigin: true);
        await using var _ = provider;
        var origin = scope.ServiceProvider.GetRequiredService<WriteOriginContext>();
        var queue = new AgentEventQueue();

        await agent.ExecuteAsync(
            Context(taskId: "t1", skill: "write_knowledge", agentName: "Devin"),
            queue, CancellationToken.None);
        await DrainAsync(queue);

        Assert.Equal("a2a", origin.Channel);
        Assert.Equal("Devin", origin.AgentName);
        scope.Dispose();
    }
}
