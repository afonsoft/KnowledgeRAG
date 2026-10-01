using A2A;
using KnowledgeHub.Server.A2A;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = A2A.TaskStatus;

namespace KnowledgeHub.Tests.Unit.A2a;

/// <summary>
/// SPEC-20261001-a2a-task-durability: durable task store (RF-001), push-config
/// persistence + terminal dispatch (RF-003), frontmatter origin (RF-004) and
/// Agent Card modes/capabilities (RF-005).
/// </summary>
public sealed class A2aTaskDurabilityTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), StoreFileName());
    private readonly RecordingNotifier _notifier = new();

    private static string StoreFileName() => $"kh-a2a-store-{Guid.NewGuid():N}.db";

    public A2aTaskDurabilityTests()
    {
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
    }

    private KnowledgeHubDbContext Open() => new(
        new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private EfA2aTaskStore Store() => new(
        new CatalogDatabase(CatalogProvider.Sqlite, null),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
            .Build(),
        new HttpContextAccessor(),
        _notifier,
        NullLogger<EfA2aTaskStore>.Instance);

    private static AgentTask MkTask(string id, string contextId, TaskState state) => new()
    {
        Id = id,
        ContextId = contextId,
        Status = new TaskStatus { State = state, Timestamp = DateTimeOffset.UtcNow },
        History = [new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("hi")] }],
        Artifacts = state == TaskState.Completed
            ? [new Artifact { ArtifactId = "a1", Parts = [Part.FromText("done")] }]
            : null
    };

    // ---- RF-001: durable task store ------------------------------------

    [Fact]
    public async Task SaveThenGet_PersistsTask_AcrossInstances()
    {
        await Store().SaveTaskAsync("t1", MkTask("t1", "ctx-1", TaskState.Completed));
        // Fresh instance = restart simulation at the unit level.
        var reloaded = await Store().GetTaskAsync("t1");
        Assert.NotNull(reloaded);
        Assert.Equal("ctx-1", reloaded.ContextId);
        Assert.Equal(TaskState.Completed, reloaded.Status.State);
        Assert.NotNull(reloaded.Artifacts);
        Assert.Equal("a1", reloaded.Artifacts![0].ArtifactId);
    }

    [Fact]
    public async Task GetTask_Unknown_ReturnsNull()
        => Assert.Null(await Store().GetTaskAsync("nope"));

    [Fact]
    public async Task DeleteTask_RemovesRow()
    {
        await Store().SaveTaskAsync("t1", MkTask("t1", "ctx-1", TaskState.Working));
        await Store().DeleteTaskAsync("t1");
        Assert.Null(await Store().GetTaskAsync("t1"));
    }

    [Fact]
    public async Task ListTasks_FiltersAndPaginates()
    {
        var store = Store();
        for (var i = 0; i < 5; i++)
            await store.SaveTaskAsync($"t{i}",
                MkTask($"t{i}", i < 3 ? "ctx-a" : "ctx-b",
                    i < 2 ? TaskState.Completed : TaskState.Working));

        var all = await store.ListTasksAsync(new ListTasksRequest());
        Assert.Equal(5, all.TotalSize);

        var byContext = await store.ListTasksAsync(new ListTasksRequest { ContextId = "ctx-a" });
        Assert.Equal(3, byContext.Tasks.Count);

        var byStatus = await store.ListTasksAsync(new ListTasksRequest { Status = TaskState.Completed });
        Assert.Equal(2, byStatus.Tasks.Count);
        Assert.All(byStatus.Tasks, t => Assert.Equal(TaskState.Completed, t.Status.State));

        var page1 = await store.ListTasksAsync(new ListTasksRequest { PageSize = 2 });
        Assert.Equal(2, page1.Tasks.Count);
        Assert.False(string.IsNullOrEmpty(page1.NextPageToken));
        var page2 = await store.ListTasksAsync(
            new ListTasksRequest { PageSize = 2, PageToken = page1.NextPageToken });
        Assert.Equal(2, page2.Tasks.Count);
        Assert.NotEqual(page1.Tasks.Select(t => t.Id), page2.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ListTasks_HistoryLengthAndArtifactsOptions()
    {
        var task = MkTask("t1", "ctx-1", TaskState.Completed);
        task.History = Enumerable.Range(0, 5).Select(i => new Message
        {
            MessageId = $"m{i}",
            Role = Role.User,
            Parts = [Part.FromText($"msg{i}")]
        }).ToList();
        await Store().SaveTaskAsync("t1", task);

        var trimmed = await Store().ListTasksAsync(
            new ListTasksRequest { HistoryLength = 2, IncludeArtifacts = false });
        var row = Assert.Single(trimmed.Tasks);
        Assert.Equal(2, row.History!.Count);
        Assert.Equal("m4", row.History[1].MessageId);
        Assert.Null(row.Artifacts);
    }

    [Fact]
    public async Task Purge_RemovesOldTasks_KeepsRecent()
    {
        var store = Store();
        await store.SaveTaskAsync("old", MkTask("old", "c", TaskState.Completed));
        await store.SaveTaskAsync("new", MkTask("new", "c", TaskState.Working));
        using (var db = Open())
        {
            var row = await db.A2aTasks.SingleAsync(t => t.TaskId == "old");
            row.CreatedAt = DateTimeOffset.UtcNow.AddDays(-4);
            await db.SaveChangesAsync();
        }
        var purged = await store.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddHours(-72));
        Assert.Equal(1, purged);
        Assert.Null(await store.GetTaskAsync("old"));
        Assert.NotNull(await store.GetTaskAsync("new"));
    }

    [Fact]
    public async Task ListTasks_StatusTimestampAfter_Filters()
    {
        var store = Store();
        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Working));
        var marker = DateTimeOffset.UtcNow;
        await store.SaveTaskAsync("t2", MkTask("t2", "c", TaskState.Completed));

        var filtered = await store.ListTasksAsync(
            new ListTasksRequest { StatusTimestampAfter = marker });
        Assert.Single(filtered.Tasks);
        Assert.Equal("t2", filtered.Tasks[0].Id);
    }

    [Fact]
    public async Task SaveTask_CapturesCallerKeyId_FromHttpContext()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(
                            KnowledgeHub.Server.Auth.ApiKeyAuthenticationHandler
                                .KeyIdClaim, "key-42")],
                        "test"))
            }
        };
        var store = new EfA2aTaskStore(
            new CatalogDatabase(CatalogProvider.Sqlite, null),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
                .Build(),
            accessor,
            _notifier,
            NullLogger<EfA2aTaskStore>.Instance);
        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Working));

        using var db = Open();
        Assert.Equal("key-42", (await db.A2aTasks.SingleAsync()).CallerKeyId);
    }

    [Fact]
    public async Task DeleteTask_Unknown_NoOp()
    {
        var store = Store();
        await store.DeleteTaskAsync("ghost"); // must not throw
        Assert.Null(await store.GetTaskAsync("ghost"));
    }

    // ---- RF-003: push configs + terminal dispatch ------------------------

    private static TaskPushNotificationConfig Config(string id, string url) => new()
    {
        Id = id,
        TaskId = "t1",
        PushNotificationConfig = new PushNotificationConfig { Url = url, Token = "tok" }
    };

    [Fact]
    public async Task PushConfig_UpsertGetDelete()
    {
        var store = Store();
        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Working));

        await store.UpsertPushConfigAsync("t1", Config("cfg1", "https://hooks.example/a"));
        await store.UpsertPushConfigAsync("t1", Config("cfg2", "https://hooks.example/b"));
        var configs = await store.GetPushConfigsAsync("t1");
        Assert.Equal(2, configs.Count);

        // Upsert by id replaces, not appends.
        await store.UpsertPushConfigAsync("t1", Config("cfg1", "https://hooks.example/c"));
        configs = await store.GetPushConfigsAsync("t1");
        Assert.Equal(2, configs.Count);
        Assert.Equal("https://hooks.example/c",
            configs.Single(c => c.Id == "cfg1").PushNotificationConfig.Url);

        await store.DeletePushConfigAsync("t1", "cfg1");
        configs = await store.GetPushConfigsAsync("t1");
        Assert.Single(configs);

        await Assert.ThrowsAsync<A2AException>(
            () => store.DeletePushConfigAsync("t1", "missing"));
        await Assert.ThrowsAsync<A2AException>(
            () => store.UpsertPushConfigAsync("ghost", Config("c", "https://x")));
    }

    [Fact]
    public async Task TerminalTransition_DispatchesPush_ExactlyOnce()
    {
        var store = Store();
        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Working));
        await store.UpsertPushConfigAsync("t1", Config("cfg1", "https://hooks.example/a"));

        // Working saves don't dispatch.
        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Working));
        Assert.Empty(_notifier.Calls);

        // Working → Completed dispatches the task + configs.
        var done = MkTask("t1", "c", TaskState.Completed);
        await store.SaveTaskAsync("t1", done);
        var call = Assert.Single(_notifier.Calls);
        Assert.Equal("t1", call.Task.Id);
        Assert.Equal("cfg1", call.Configs[0].Id);

        // A later save of the already-terminal task must not re-notify.
        await store.SaveTaskAsync("t1", done);
        Assert.Single(_notifier.Calls);
    }

    private sealed class RecordingNotifier : IA2aPushNotifier
    {
        public List<(AgentTask Task, IReadOnlyList<TaskPushNotificationConfig> Configs)> Calls { get; } = [];
        public Task DispatchTerminalAsync(
            AgentTask task, IReadOnlyList<TaskPushNotificationConfig> configs,
            CancellationToken cancellationToken)
        {
            Calls.Add((task, configs));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TerminalDispatch_NotifierThrows_SaveStillSucceeds()
    {
        // Dispatch is fire-and-forget — a notifier failure must not break saves.
        var store = new EfA2aTaskStore(
            new CatalogDatabase(CatalogProvider.Sqlite, null),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
                .Build(),
            new HttpContextAccessor(),
            new ThrowingNotifier(),
            NullLogger<EfA2aTaskStore>.Instance);

        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Working));
        await store.UpsertPushConfigAsync("t1", Config("cfg", "https://hooks.example/x"));
        await store.SaveTaskAsync("t1", MkTask("t1", "c", TaskState.Completed));

        var reloaded = await store.GetTaskAsync("t1");
        Assert.Equal(TaskState.Completed, reloaded!.Status.State);
    }

    [Fact]
    public async Task GetPushConfigs_UnknownTask_ReturnsEmpty()
        => Assert.Empty(await Store().GetPushConfigsAsync("ghost"));

    private sealed class ThrowingNotifier : IA2aPushNotifier
    {
        public Task DispatchTerminalAsync(
            AgentTask task, IReadOnlyList<TaskPushNotificationConfig> configs,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    // ---- RF-004: write provenance frontmatter ----------------------------

    [Fact]
    public void WithFrontmatter_Origin_StampsChannelKeyAgent()
    {
        var body = ObsidianNoteWriter.WithFrontmatter("# Doc", null,
            new WriteOriginContext { Channel = "a2a", KeyId = "key-1", AgentName = "Devin" });
        Assert.StartsWith("---\n", body);
        Assert.Contains("origin:", body);
        Assert.Contains("channel: \"a2a\"", body);
        Assert.Contains("keyId: \"key-1\"", body);
        Assert.Contains("agentName: \"Devin\"", body);
        Assert.Contains("at: \"", body);
        Assert.EndsWith("# Doc", body);
    }

    [Fact]
    public void WithFrontmatter_NoTagsNoOrigin_ReturnsContentVerbatim()
        => Assert.Equal("# Doc", ObsidianNoteWriter.WithFrontmatter("# Doc", null));

    [Fact]
    public void WithFrontmatter_Origin_EscapesQuotes()
    {
        var body = ObsidianNoteWriter.WithFrontmatter("x", null,
            new WriteOriginContext { Channel = "mcp", AgentName = "evil\"name" });
        Assert.Contains("agentName: \"evil\\\"name\"", body);
    }

    // ---- RF-005: agent card ----------------------------------------------

    [Fact]
    public void AgentCard_SkillsAdvertiseModes_PushFollowsFlag()
    {
        var enabled = A2AEndpointExtensions.BuildAgentCard(new Uri("http://h/"), pushEnabled: true);
        Assert.True(enabled.Capabilities.PushNotifications);
        var disabled = A2AEndpointExtensions.BuildAgentCard(new Uri("http://h/"), pushEnabled: false);
        Assert.False(disabled.Capabilities.PushNotifications);

        var skills = enabled.Skills.ToDictionary(s => s.Id);
        Assert.Equal(["text/plain"], skills["agent_chat"].InputModes);
        Assert.Equal(["application/json"], skills["read_document"].InputModes);
        Assert.Equal(["text/plain", "application/json"], skills["ask_knowledge"].InputModes);
        Assert.All(skills.Values, s => Assert.NotNull(s.OutputModes));
    }
}
