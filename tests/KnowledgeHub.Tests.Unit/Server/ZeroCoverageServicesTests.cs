using System.Security.Claims;
using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// Audit 2026-10-03: unit coverage for four services that sat at 0% — the
/// integration suite exercises them but the ratchet gate only measures this
/// project.
/// </summary>
public sealed class ZeroCoverageServicesTests : IDisposable
{
    private readonly string _dbPath =
        Path.Join(Path.GetTempPath(), $"kh-zero-{Guid.NewGuid():N}.db");

    public ZeroCoverageServicesTests()
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

    private EfMcpTaskStore TaskStore() => new(
        new CatalogDatabase(CatalogProvider.Sqlite, null),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
            .Build());

    // ---------- EfMcpTaskStore ----------

    [Fact]
    public async Task TaskStore_CreateThenGet_RoundTrips()
    {
        var store = TaskStore();
        var created = await store.CreateTaskAsync();

        Assert.StartsWith("mt_", created.TaskId);
        Assert.Equal(McpTaskStatus.Working, created.Status);

        var fetched = await store.GetTaskAsync(created.TaskId);
        Assert.NotNull(fetched);
        Assert.Equal(created.TaskId, fetched!.TaskId);
        Assert.Equal(store.DefaultPollIntervalMs, fetched.PollIntervalMs);
        Assert.Equal(store.DefaultTimeToLive, fetched.TimeToLive);
    }

    [Fact]
    public async Task TaskStore_Get_ExpiredRowReturnsNull()
    {
        var store = TaskStore();
        var created = await store.CreateTaskAsync();
        await using var db = Open();
        var row = await db.McpTasks.FirstAsync(t => t.TaskId == created.TaskId);
        row.CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await db.SaveChangesAsync();

        Assert.Null(await store.GetTaskAsync(created.TaskId));
        Assert.Null(await store.GetTaskAsync("mt_missing"));
    }

    [Fact]
    public async Task TaskStore_CompletedFailedAndCancelled_Transitions()
    {
        var store = TaskStore();
        var done = await store.CreateTaskAsync();
        await store.SetCompletedAsync(done.TaskId, JsonDocument.Parse("{\"ok\":true}").RootElement);
        var completed = await store.GetTaskAsync(done.TaskId);
        Assert.Equal(McpTaskStatus.Completed, completed!.Status);
        Assert.True(completed.Result!.Value.GetProperty("ok").GetBoolean());

        var failed = await store.CreateTaskAsync();
        await store.SetFailedAsync(failed.TaskId, JsonDocument.Parse("{\"msg\":\"boom\"}").RootElement);
        var failedInfo = await store.GetTaskAsync(failed.TaskId);
        Assert.Equal(McpTaskStatus.Failed, failedInfo!.Status);

        var pending = await store.CreateTaskAsync();
        Assert.True(await store.SetCancelledAsync(pending.TaskId));
        // Terminal rows don't re-cancel; missing rows neither.
        Assert.False(await store.SetCancelledAsync(pending.TaskId));
        Assert.False(await store.SetCancelledAsync(done.TaskId));
        Assert.False(await store.SetCancelledAsync("mt_missing"));
    }

    [Fact]
    public async Task TaskStore_InputRequests_RoundTripAndResolve()
    {
        var store = TaskStore();
        InputResponseReceivedEventArgs? received = null;
        store.InputResponseReceived += e => received = e;

        var task = await store.CreateTaskAsync();
        var requests = new Dictionary<string, InputRequest>
        {
            ["r1"] = new() { Method = "elicitation/create" }
        };
        await store.SetInputRequestsAsync(task.TaskId, requests);

        var pending = await store.GetTaskAsync(task.TaskId);
        Assert.Equal(McpTaskStatus.InputRequired, pending!.Status);
        Assert.Equal("elicitation/create", pending.InputRequests!["r1"].Method);

        await store.ResolveInputRequestsAsync(task.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = new() });

        var resumed = await store.GetTaskAsync(task.TaskId);
        Assert.Equal(McpTaskStatus.Working, resumed!.Status);
        Assert.Null(resumed.InputRequests);
        Assert.Equal(task.TaskId, received?.TaskId);
        Assert.Equal("r1", received?.RequestId);
    }

    // ---------- SqlitePragmas ----------

    [Fact]
    public async Task SqlitePragmas_ApplyAsync_SetsHintsOnSqliteConnection()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        await SqlitePragmas.ApplyAsync(conn, CancellationToken.None);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous;";
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!); // 1 = NORMAL
        cmd.CommandText = "PRAGMA temp_store;";
        Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!); // 2 = MEMORY
    }

    [Fact]
    public async Task SqlitePragmas_NonSqliteConnection_IsNoOp()
    {
        var conn = new FakeConnection();
        await SqlitePragmas.ApplyAsync(conn, CancellationToken.None);
        Assert.False(conn.CommandCreated);
    }

    private sealed class FakeConnection : System.Data.Common.DbConnection
    {
        public bool CommandCreated;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override System.Data.Common.DbCommand CreateDbCommand()
        {
            CommandCreated = true;
            throw new NotSupportedException();
        }
    }

    // ---------- CallerScopeProvider ----------

    private static ClaimsPrincipal ApiKeyPrincipal(Guid keyId) => new(
        new ClaimsIdentity(
            [new Claim(ApiKeyAuthenticationHandler.AuthMethodClaim, "apikey"),
             new Claim(ApiKeyAuthenticationHandler.KeyIdClaim, keyId.ToString())]));

    // ApiKey.UserId and ApiKeyUsageEvent.ApiKeyId are real FKs — every fixture
    // that touches either needs the user + key rows.
    private async Task<Guid> SeedKeyAsync()
    {
        var keyId = Guid.NewGuid();
        await using var db = Open();
        db.Users.Add(new AppUser { Username = "u", PasswordHash = "h" });
        await db.SaveChangesAsync();
        var user = await db.Users.SingleAsync();
        db.ApiKeys.Add(new ApiKey
        {
            Id = keyId,
            UserId = user.Id,
            Name = "k",
            KeyHash = new string('a', 64),
            Prefix = "aft_xxxxxxxx"
        });
        await db.SaveChangesAsync();
        return keyId;
    }

    [Fact]
    public async Task CallerScope_ApiKeyPrincipal_LoadsScopeFromRow()
    {
        var sourceId = Guid.NewGuid();
        var keyId = await SeedKeyAsync();
        await using (var db = Open())
        {
            var key = await db.ApiKeys.SingleAsync(k => k.Id == keyId);
            key.AllowedSourceIdsJson = $"[\"{sourceId}\"]";
            key.AllowedToolsJson = "[\"search_knowledge\"]";
            key.AllowWrite = false;
            await db.SaveChangesAsync();
        }

        var http = new DefaultHttpContext { User = ApiKeyPrincipal(keyId) };
        var provider = new CallerScopeProvider(
            new HttpContextAccessor { HttpContext = http },
            new MemoryCache(new MemoryCacheOptions()),
            Open());

        var scope = await provider.GetAsync(CancellationToken.None);
        Assert.False(scope.IsUnrestricted);
        Assert.True(scope.AllowsSource(sourceId));
        Assert.False(scope.AllowsSource(Guid.NewGuid()));

        // Second call hits the cached scope.
        var again = await provider.GetAsync(CancellationToken.None);
        Assert.Same(scope, again);
    }

    [Fact]
    public async Task CallerScope_NonApiKeyAndMissingRow_AreUnrestricted()
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var provider = new CallerScopeProvider(
            new HttpContextAccessor { HttpContext = http },
            new MemoryCache(new MemoryCacheOptions()),
            Open());
        Assert.Same(CallerScope.Unrestricted, await provider.GetAsync(CancellationToken.None));

        var keyed = new DefaultHttpContext { User = ApiKeyPrincipal(Guid.NewGuid()) };
        var provider2 = new CallerScopeProvider(
            new HttpContextAccessor { HttpContext = keyed },
            new MemoryCache(new MemoryCacheOptions()),
            Open());
        Assert.True((await provider2.GetAsync(CancellationToken.None)).IsUnrestricted);
    }

    // ---------- ApiKeyUsageMiddleware ----------

    [Fact]
    public async Task ApiKeyUsage_RecordsEventForApiKeyPrincipal()
    {
        var db = Open();
        var keyId = await SeedKeyAsync();
        var mw = new ApiKeyUsageMiddleware(_ => Task.CompletedTask, NullLogger<ApiKeyUsageMiddleware>.Instance);

        var ctx = new DefaultHttpContext { User = ApiKeyPrincipal(keyId) };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/mcp";
        ctx.Request.Headers.UserAgent = "test-agent";
        ctx.Response.StatusCode = 201;

        await mw.InvokeAsync(ctx, db);

        var ev = await db.ApiKeyUsageEvents.SingleAsync();
        Assert.Equal(keyId, ev.ApiKeyId);
        Assert.Equal("POST", ev.HttpMethod);
        Assert.Equal("/mcp", ev.Path);
        Assert.Equal(201, ev.StatusCode);
        Assert.Equal("test-agent", ev.UserAgent);
    }

    [Fact]
    public async Task ApiKeyUsage_SkipsNonApiKeyPrincipals()
    {
        var db = Open();
        var mw = new ApiKeyUsageMiddleware(_ => Task.CompletedTask, NullLogger<ApiKeyUsageMiddleware>.Instance);

        await mw.InvokeAsync(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }, db);

        Assert.Empty(await db.ApiKeyUsageEvents.ToListAsync());
    }

    [Fact]
    public async Task ApiKeyUsage_SweepDeletesEventsOlderThanRetention()
    {
        var db = Open();
        var keyId = await SeedKeyAsync();
        db.ApiKeyUsageEvents.Add(new ApiKeyUsageEvent
        {
            ApiKeyId = keyId,
            Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(95),
            HttpMethod = "GET",
            Path = "/old",
            StatusCode = 200
        });
        await db.SaveChangesAsync();

        var mw = new ApiKeyUsageMiddleware(_ => Task.CompletedTask, NullLogger<ApiKeyUsageMiddleware>.Instance);
        var ctx = new DefaultHttpContext { User = ApiKeyPrincipal(keyId) };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/mcp";

        await mw.InvokeAsync(ctx, db);

        var remaining = await db.ApiKeyUsageEvents.Select(e => e.Path).ToListAsync();
        Assert.Equal(["/mcp"], remaining);
    }
}
