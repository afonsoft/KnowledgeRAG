using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261113-memory-bank-merge RF-001..RF-005: transactional re-parenting,
/// target-wins collisions, MergedInto redirect with cycle guard, idempotent
/// re-run, list tombstones, repo fallback on effective bank resolution.
/// </summary>
public class MemoryBankMergeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-merge-{Guid.NewGuid():N}.db");

    public MemoryBankMergeTests()
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

    private MemoryBankMergeService MergeSvc(KnowledgeHubDbContext db) =>
        new(db, Banks(db));

    private async Task<MemoryBank> MakeBank(KnowledgeHubDbContext db, string key)
    {
        var b = await Banks(db).CreateAsync(key, key, null, null, null, default);
        return b;
    }

    [Fact]
    public async Task AC001_merge_reparents_all_bank_scoped_rows_with_audit()
    {
        using var db = Open();
        var a = await MakeBank(db, "a");
        var b = await MakeBank(db, "b");
        db.MemoryUnits.Add(new MemoryUnit { BankId = a.Id, Text = "fato", FactType = "world", ContentHash = "h1", MentionedAt = DateTime.UtcNow });
        db.MemoryDocuments.Add(new MemoryDocument { BankId = a.Id, ExternalId = "d1", ContentHash = "x" });
        db.MemoryDirectives.Add(new MemoryDirective { BankId = a.Id, Name = "dir", Content = "c", Priority = 5 });
        db.MemoryOperations.Add(new MemoryOperation { BankId = a.Id, PayloadJson = "{}", Status = "succeeded" });
        await db.SaveChangesAsync(default);

        var result = await MergeSvc(db).MergeAsync("a", "b", default);

        Assert.True(result.Merged);
        Assert.False(result.AlreadyMerged);
        Assert.Equal(b.Id, result.TargetBankId);
        Assert.Equal(1, result.Moved["units"]);
        Assert.Equal(1, result.Moved["documents"]);
        Assert.Equal(1, result.Moved["directives"]);
        Assert.Equal(1, result.Moved["operations"]); // pre-existing op re-parented
        // audit op itself is a NEW row on the target — verified below
        var src = await db.MemoryBanks.SingleAsync(x => x.BankKey == "a");
        Assert.Equal(b.Id, src.MergedIntoBankId);
        var audit = await db.MemoryOperations
            .Where(o => o.Kind == "bank_merge" && o.BankId == b.Id).SingleAsync();
        Assert.Equal("succeeded", audit.Status);
        Assert.Contains("units", audit.ResultJson);
    }

    [Fact]
    public async Task AC002_old_key_resolves_to_merge_target()
    {
        using var db = Open();
        var a = await MakeBank(db, "oldname");
        var b = await MakeBank(db, "newname");
        await MergeSvc(db).MergeAsync("oldname", "newname", default);

        var resolved = await Banks(db).ResolveAsync("oldname", default, create: false);
        Assert.Equal(b.Id, resolved.Id);
    }

    [Fact]
    public async Task AC003_content_hash_collision_target_wins_source_soft_deleted()
    {
        using var db = Open();
        var a = await MakeBank(db, "a");
        var b = await MakeBank(db, "b");
        db.MemoryUnits.AddRange(
            new MemoryUnit { BankId = a.Id, Text = "mesmo fato", FactType = "world", ContentHash = "dup", MentionedAt = DateTime.UtcNow },
            new MemoryUnit { BankId = b.Id, Text = "mesmo fato", FactType = "world", ContentHash = "dup", MentionedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(default);

        var result = await MergeSvc(db).MergeAsync("a", "b", default);

        Assert.Equal(1, result.Skipped["units"]);
        Assert.Equal(1, await db.MemoryUnits.CountAsync(u => u.BankId == b.Id && !u.IsDeleted));
        var srcUnit = await db.MemoryUnits.SingleAsync(u => u.BankId == a.Id || u.IsDeleted);
        Assert.True(srcUnit.IsDeleted);
    }

    [Fact]
    public async Task AC004_rerun_is_idempotent_already_merged()
    {
        using var db = Open();
        await MakeBank(db, "a");
        var b = await MakeBank(db, "b");
        await MergeSvc(db).MergeAsync("a", "b", default);

        var second = await MergeSvc(db).MergeAsync("a", "b", default);

        Assert.False(second.Merged);
        Assert.True(second.AlreadyMerged);
        Assert.Equal(b.Id, second.TargetBankId);
    }

    [Fact]
    public async Task AC005_chain_resolves_to_final_target_and_cycles_rejected()
    {
        using var db = Open();
        var a = await MakeBank(db, "a");
        var b = await MakeBank(db, "b");
        var c = await MakeBank(db, "c");
        a.MergedIntoBankId = b.Id;
        b.MergedIntoBankId = c.Id;
        await db.SaveChangesAsync(default);

        var resolved = await Banks(db).ResolveAsync("a", default, create: false);
        Assert.Equal(c.Id, resolved.Id);

        // Cycle: c → a closes the loop.
        c.MergedIntoBankId = a.Id;
        await db.SaveChangesAsync(default);
        await Assert.ThrowsAsync<MemoryBadRequestException>(() =>
            Banks(db).ResolveAsync("a", default, create: false));
    }

    [Fact]
    public async Task AC006_repo_fallback_derives_bank_key()
    {
        using var db = Open();
        var banks = Banks(db);
        var bank = await banks.CreateAsync("repo:org/myrepo", "r", null, null, null, default);

        var effective = MemoryBankContext.EffectiveKey(
            new ServiceCollection().BuildServiceProvider(), null, "github.com/Org/MyRepo.git");
        Assert.Equal("repo:org/myrepo", effective);
        var resolved = await banks.ResolveAsync(effective, default, create: false);
        Assert.Equal(bank.Id, resolved.Id);
    }

    [Fact]
    public async Task AC007_bankkey_wins_over_repo_and_malformed_falls_back()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        Assert.Equal("explicit",
            MemoryBankContext.EffectiveKey(sp, "explicit", "org/repo"));
        Assert.Null(MemoryBankContext.EffectiveKey(sp, null, "not a repo"));
        Assert.Null(MemoryBankContext.EffectiveKey(sp, null, null));
    }

    [Fact]
    public async Task AC008_list_hides_tombstones_unless_include_merged()
    {
        using var db = Open();
        await MakeBank(db, "a");
        await MakeBank(db, "b");
        await MergeSvc(db).MergeAsync("a", "b", default);

        var banks = Banks(db);
        var visible = await banks.ListAsync(false, default);
        Assert.DoesNotContain(visible, x => x.BankKey == "a");
        var all = await banks.ListAsync(true, default);
        var tomb = Assert.Single(all, x => x.BankKey == "a");
        Assert.True(tomb.Merged);
    }

    [Fact]
    public async Task EDGE_self_merge_and_missing_bank_rejected()
    {
        using var db = Open();
        await MakeBank(db, "a");
        await Assert.ThrowsAsync<MemoryBadRequestException>(() =>
            MergeSvc(db).MergeAsync("a", "a", default));
        await Assert.ThrowsAsync<MemoryNotFoundException>(() =>
            MergeSvc(db).MergeAsync("ghost", "a", default));
        // Already merged to a different target → 400 naming real target.
        var b = await MakeBank(db, "b");
        var c = await MakeBank(db, "c");
        await MergeSvc(db).MergeAsync("a", "b", default);
        var ex = await Assert.ThrowsAsync<MemoryBadRequestException>(() =>
            MergeSvc(db).MergeAsync("a", "c", default));
        Assert.Contains("b", ex.Message);
    }
}
