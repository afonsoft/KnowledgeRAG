using System.Net;
using A2A;
using KnowledgeHub.Server.A2A;
using KnowledgeHub.Server.Audit.Evidence;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = A2A.TaskStatus;

namespace KnowledgeHub.Tests.Unit.A2a;

/// <summary>
/// SPEC-20261001-a2a-task-durability RF-003: webhook delivery (signature
/// header, bounded retry), push-config CRUD overrides on the A2A server,
/// and egress screening of webhook URLs.
/// </summary>
public sealed class A2aPushAndServerTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"kh-a2a-push-{Guid.NewGuid():N}.db");
    private readonly RecordingNotifier _notifier = new();

    public A2aPushAndServerTests()
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
        Config(("Database:Path", _dbPath)),
        new HttpContextAccessor(),
        _notifier,
        NullLogger<EfA2aTaskStore>.Instance);

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    private KnowledgeHubA2AServer Server(bool pushEnabled = true) => new(
        new NullAgent(),
        Store(),
        new ChannelEventNotifier(),
        NullLogger<A2AServer>.Instance,
        new A2AServerOptions(),
        Config(("A2a:PushNotifications:Enabled", pushEnabled ? "true" : "false")),
        _notifier,
        NullLogger<KnowledgeHubA2AServer>.Instance);

    private static AgentTask MkTask(string id, TaskState state) => new()
    {
        Id = id,
        ContextId = "ctx",
        Status = new TaskStatus { State = state, Timestamp = DateTimeOffset.UtcNow }
    };

    private static CreateTaskPushNotificationConfigRequest CreateReq(
        string taskId, string id, string url) => new()
    {
        TaskId = taskId,
        ConfigId = id,
        Config = new PushNotificationConfig { Url = url, Token = "tok" }
    };

    private sealed class NullAgent : IAgentHandler
    {
        public Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingNotifier : IA2aPushNotifier
    {
        public List<AgentTask> Calls { get; } = [];
        public Task DispatchTerminalAsync(AgentTask task,
            IReadOnlyList<TaskPushNotificationConfig> configs, CancellationToken ct)
        {
            Calls.Add(task);
            return Task.CompletedTask;
        }
    }

    // ---- KnowledgeHubA2AServer: push-config CRUD --------------------------

    [Fact]
    public async Task Server_PushConfig_CrudRoundTrip()
    {
        var server = Server();
        await Store().SaveTaskAsync("t1", MkTask("t1", TaskState.Working));

        var created = await server.CreateTaskPushNotificationConfigAsync(
            CreateReq("t1", "cfg-1", "https://hooks.example/a"));
        Assert.Equal("cfg-1", created.Id);
        Assert.Equal("t1", created.TaskId);

        var got = await server.GetTaskPushNotificationConfigAsync(
            new GetTaskPushNotificationConfigRequest { TaskId = "t1", Id = "cfg-1" });
        Assert.Equal("https://hooks.example/a", got.PushNotificationConfig.Url);

        var list = await server.ListTaskPushNotificationConfigAsync(
            new ListTaskPushNotificationConfigRequest { TaskId = "t1" });
        Assert.Single(list.Configs!);

        await server.DeleteTaskPushNotificationConfigAsync(
            new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "cfg-1" });
        Assert.Empty((await server.ListTaskPushNotificationConfigAsync(
            new ListTaskPushNotificationConfigRequest { TaskId = "t1" })).Configs!);
    }

    [Fact]
    public async Task Server_PushConfig_UnknownTaskOrConfig_Throws()
    {
        var server = Server();
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            server.CreateTaskPushNotificationConfigAsync(
                CreateReq("ghost", "c", "https://hooks.example/x")));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.ErrorCode);

        await Store().SaveTaskAsync("t1", MkTask("t1", TaskState.Working));
        var missing = await Assert.ThrowsAsync<A2AException>(() =>
            server.GetTaskPushNotificationConfigAsync(
                new GetTaskPushNotificationConfigRequest { TaskId = "t1", Id = "nope" }));
        Assert.Equal(A2AErrorCode.TaskNotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task Server_Create_RejectsBlockedWebhookUrl()
    {
        var server = Server();
        await Store().SaveTaskAsync("t1", MkTask("t1", TaskState.Working));

        // Cloud metadata endpoint is always blocked — even over https.
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            server.CreateTaskPushNotificationConfigAsync(
                CreateReq("t1", "c", "https://169.254.169.254/latest")));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.ErrorCode);

        // Plain http is rejected unless private egress is opted in.
        var http = await Assert.ThrowsAsync<A2AException>(() =>
            server.CreateTaskPushNotificationConfigAsync(
                CreateReq("t1", "c2", "http://hooks.example/x")));
        Assert.Equal(A2AErrorCode.InvalidParams, http.ErrorCode);
    }

    [Fact]
    public async Task Server_Create_OnTerminalTask_DispatchesImmediately()
    {
        var server = Server();
        await Store().SaveTaskAsync("t1", MkTask("t1", TaskState.Completed));

        await server.CreateTaskPushNotificationConfigAsync(
            CreateReq("t1", "cfg", "https://hooks.example/done"));

        // Fire-and-forget dispatch — wait briefly for the background call.
        for (var i = 0; i < 50 && _notifier.Calls.Count == 0; i++)
            await Task.Delay(20);
        Assert.Single(_notifier.Calls);
        Assert.Equal("t1", _notifier.Calls[0].Id);
    }

    [Fact]
    public async Task Server_Disabled_PushMethodsThrowUnsupported()
    {
        var server = Server(pushEnabled: false);
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            server.CreateTaskPushNotificationConfigAsync(
                CreateReq("t1", "c", "https://hooks.example/x")));
        Assert.Equal(A2AErrorCode.PushNotificationNotSupported, ex.ErrorCode);
    }

    // ---- A2aPushNotifier: delivery ----------------------------------------

    private sealed class StubEvidence : IEvidenceChainService
    {
        public string KeyId => "test";
        public Task<string> SignPayloadAsync(string payload, CancellationToken ct) =>
            Task.FromResult("hmac-sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
        public Task<EvidenceReceipt> AppendAsync(EvidenceEvent ev, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<EvidenceReceipt>> GetSessionReceiptsAsync(
            string sessionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EvidenceVerification> VerifyAsync(string sessionId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EvidenceVerification> VerifyReceiptsAsync(
            IReadOnlyList<EvidenceReceipt> receipts, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(string Url, string? Signature, string? Token, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-KH-Signature", out var s) ? s.First() : null,
                request.Headers.TryGetValues("X-A2A-Notification-Token", out var t) ? t.First() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static IServiceScopeFactory Scopes(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private A2aPushNotifier Notifier(HttpMessageHandler handler, bool withEvidence = true) => new(
        new StubFactory(handler),
        Scopes(s => { if (withEvidence) s.AddSingleton<IEvidenceChainService>(new StubEvidence()); }),
        NullLogger<A2aPushNotifier>.Instance);

    private static TaskPushNotificationConfig Webhook(string url, string? token = null) => new()
    {
        Id = "cfg",
        TaskId = "t1",
        PushNotificationConfig = new PushNotificationConfig { Url = url, Token = token }
    };

    [Fact]
    public async Task Dispatch_PostsSignedBody_WithTokenEcho()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var notifier = Notifier(handler);
        var task = MkTask("t1", TaskState.Completed);

        await notifier.DispatchTerminalAsync(task, [Webhook("https://hooks.example/ok", "tok-9")],
            CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://hooks.example/ok", req.Url);
        Assert.Equal("tok-9", req.Token);
        Assert.StartsWith("hmac-sha256:", req.Signature);
        Assert.Contains("\"id\":\"t1\"", req.Body);
    }

    [Fact]
    public async Task Dispatch_ServerError_RetriesExactly3_ThenGivesUp()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var notifier = Notifier(handler);

        await notifier.DispatchTerminalAsync(
            MkTask("t1", TaskState.Failed), [Webhook("https://hooks.example/fail")],
            CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Dispatch_NoEvidenceService_PostsUnsigned()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var notifier = Notifier(handler, withEvidence: false);

        await notifier.DispatchTerminalAsync(
            MkTask("t1", TaskState.Canceled), [Webhook("https://hooks.example/x")],
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Null(handler.Requests[0].Signature);
    }

    // ---- EgressPolicyHandler.IsBlockedAsync --------------------------------

    [Fact]
    public async Task IsBlocked_BlocksMetadataAndSchemes()
    {
        Assert.True(await EgressPolicyHandler.IsBlockedAsync(
            new Uri("file:///etc/passwd"), allowPrivateNetworks: true));
        Assert.True(await EgressPolicyHandler.IsBlockedAsync(
            new Uri("https://169.254.169.254/latest"), allowPrivateNetworks: true));
        Assert.True(await EgressPolicyHandler.IsBlockedAsync(
            new Uri("https://127.0.0.1/hook"), allowPrivateNetworks: false));
        Assert.False(await EgressPolicyHandler.IsBlockedAsync(
            new Uri("https://127.0.0.1/hook"), allowPrivateNetworks: true));
        Assert.False(await EgressPolicyHandler.IsBlockedAsync(
            new Uri("https://8.8.8.8/hook"), allowPrivateNetworks: false));
    }
}
