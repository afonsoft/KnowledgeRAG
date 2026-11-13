using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261114-memory-session-lifecycle: begin/end/list over MemoryOperation
/// kind="session" rows — idempotency, sessionId charset (PayloadJson contains
/// lookup must stay exact), defense-screened summary, context digest surfacing.
/// </summary>
public class MemorySessionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-sess-{Guid.NewGuid():N}.db");

    public MemorySessionTests()
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

    private sealed class FakeScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private MemoryBankService Banks(KnowledgeHubDbContext db) =>
        new(db, new FakeScope(CallerScope.Unrestricted));

    private MemorySessionService Svc(KnowledgeHubDbContext db) =>
        new(db, Banks(db), new NoOpMemoryDefenseScreen());

    private async Task<MemoryBank> MakeBank(KnowledgeHubDbContext db, string key = "sess") =>
        await Banks(db).CreateAsync(key, key, null, null, null, default);

    [Fact]
    public async Task AC001_begin_creates_running_session_op()
    {
        using var db = Open();
        await MakeBank(db);

        var r = await Svc(db).BeginAsync("sess", "thread-42", "merge refactor", default);

        Assert.False(r.AlreadyOpen);
        Assert.Equal("thread-42", r.SessionId);
        var op = await db.MemoryOperations.SingleAsync(o => o.Kind == "session");
        Assert.Equal("running", op.Status);
        Assert.Contains("\"sessionId\":\"thread-42\"", op.PayloadJson);
    }

    [Fact]
    public async Task AC002_begin_twice_returns_alreadyOpen()
    {
        using var db = Open();
        await MakeBank(db);
        var svc = Svc(db);

        await svc.BeginAsync("sess", "s1", null, default);
        var second = await svc.BeginAsync("sess", "s1", null, default);

        Assert.True(second.AlreadyOpen);
        Assert.Equal(1, await db.MemoryOperations.CountAsync(o => o.Kind == "session"));
    }

    [Fact]
    public async Task AC003_begin_same_id_different_bank_independent()
    {
        using var db = Open();
        await MakeBank(db, "a");
        await MakeBank(db, "b");
        var svc = Svc(db);

        await svc.BeginAsync("a", "s1", null, default);
        var other = await svc.BeginAsync("b", "s1", null, default);

        Assert.False(other.AlreadyOpen);
        Assert.Equal(2, await db.MemoryOperations.CountAsync(o => o.Kind == "session"));
    }

    [Fact]
    public async Task AC004_end_closes_with_summary_and_is_idempotent()
    {
        using var db = Open();
        await MakeBank(db);
        var svc = Svc(db);

        await svc.BeginAsync("sess", "s1", null, default);
        var end = await svc.EndAsync("sess", "s1", "stopped at PR", default);

        Assert.True(end.Ended);
        Assert.False(end.AlreadyClosed);
        Assert.NotNull(end.DurationMs);

        var op = await db.MemoryOperations.SingleAsync(o => o.Kind == "session");
        Assert.Equal("done", op.Status);
        Assert.Contains("stopped at PR", op.ResultJson);

        var again = await svc.EndAsync("sess", "s1", null, default);
        Assert.False(again.Ended);
        Assert.True(again.AlreadyClosed);
    }

    [Fact]
    public async Task AC005_end_unknown_session_returns_alreadyClosed()
    {
        using var db = Open();
        await MakeBank(db);

        var r = await Svc(db).EndAsync("sess", "ghost-1", null, default);

        Assert.False(r.Ended);
        Assert.True(r.AlreadyClosed);
    }

    [Fact]
    public async Task AC006_session_id_charset_is_validated()
    {
        using var db = Open();
        await MakeBank(db);
        var svc = Svc(db);

        await Assert.ThrowsAsync<MemoryBadRequestException>(
            () => svc.BeginAsync("sess", "bad\"id", null, default));
        await Assert.ThrowsAsync<MemoryBadRequestException>(
            () => svc.EndAsync("sess", "a\"OR\"1", null, default));
    }

    [Fact]
    public async Task AC007_end_summary_is_defense_screened()
    {
        using var db = Open();
        var bank = await MakeBank(db);
        var blocking = new BlockingScreen();
        var svc = new MemorySessionService(db, Banks(db), blocking);

        await svc.BeginAsync("sess", "s1", null, default);
        await Assert.ThrowsAsync<MemoryDefenseException>(
            () => svc.EndAsync("sess", "s1", "key = aft_deadbeef", default));

        var op = await db.MemoryOperations.SingleAsync(o => o.Kind == "session");
        Assert.Equal("running", op.Status); // blocked summary must not close it
    }

    [Fact]
    public async Task AC008_list_filters_openOnly()
    {
        using var db = Open();
        await MakeBank(db);
        var svc = Svc(db);

        await svc.BeginAsync("sess", "open-1", null, default);
        await svc.BeginAsync("sess", "closed-1", null, default);
        await svc.EndAsync("sess", "closed-1", "done", default);

        var all = await svc.ListAsync("sess", openOnly: false, limit: 20, ct: default);
        var open = await svc.ListAsync("sess", openOnly: true, limit: 20, ct: default);

        Assert.Equal(2, all.Count);
        Assert.Single(open);
        Assert.Equal("open-1", open[0].SessionId);
    }

    private sealed class BlockingScreen : IMemoryDefenseScreen
    {
        public Task<string> ScreenAsync(string text, MemoryBank bank, CancellationToken ct) =>
            string.IsNullOrEmpty(text)
                ? Task.FromResult(text)
                : throw new MemoryDefenseException("blocked", []);
    }
}
