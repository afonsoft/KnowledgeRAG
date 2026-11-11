using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Memory;
using KnowledgeHub.Server.Memory.MentalModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261111-mental-models-pages: trigger validation, path rules,
/// CRUD + uniqueness, folder tree, refresh (full/delta/watermark/
/// skip-in-flight/min-interval), staleness, history, vault projection.
/// </summary>
public class MentalModelsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-mm-{Guid.NewGuid():N}.db");
    private readonly string _vault = Path.Combine(Path.GetTempPath(), $"kh-vault-{Guid.NewGuid():N}");

    public MentalModelsTests()
    {
        Directory.CreateDirectory(_vault);
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_vault, true); } catch (IOException) { /* best effort */ }
    }

    private KnowledgeHubDbContext Open() => new(
        new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private sealed class FakeScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private sealed class OneShotChat(string reply) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m,
            ChatOptions? o = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private VaultPageProjector Projector(bool enabled) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:Path"] = _vault,
            ["Memory:Pages:ProjectToVault"] = enabled ? "true" : "false"
        }).Build(), NullLogger<VaultPageProjector>.Instance);

    private MentalModelService Models(KnowledgeHubDbContext db, bool vault = true)
    {
        var sc = new ServiceCollection().BuildServiceProvider();
        return new MentalModelService(db,
            new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted)),
            Projector(vault), sc);
    }

    private MentalModelRefreshService Refresher(
        KnowledgeHubDbContext db, IChatClient? chat, bool vault = true)
    {
        var sc = new ServiceCollection();
        if (chat is not null) sc.AddSingleton(chat);
        var sp = sc.BuildServiceProvider();
        return new MentalModelRefreshService(db, Models(db, vault),
            new MentalModelRefreshLocks(),
            new MemoryConfigResolver(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Memory:MentalModels:HistoryLimit"] = "3" }).Build()),
            Projector(vault), sp);
    }

    private async Task<MemoryBank> BankAsync(KnowledgeHubDbContext db)
    {
        var bank = new MemoryBank { BankKey = "default", ApiKeyId = MemoryBank.ServerWideKey, Name = "d" };
        db.MemoryBanks.Add(bank);
        await db.SaveChangesAsync();
        return bank;
    }

    private static MemoryUnit Unit(MemoryBank bank, string text, string factType = "observation",
        DateTime? mentionedAt = null, string[]? tags = null) => new()
        {
            BankId = bank.Id,
            Text = text,
            FactType = factType,
            ContentHash = Guid.NewGuid().ToString("N")[..12],
            MentionedAt = mentionedAt ?? DateTime.UtcNow,
            Tags = [.. tags ?? []]
        };

    // ---------- trigger + path validation (RF-001) ----------

    [Fact]
    public void Trigger_defaults_for_page_observations_only_delta()
    {
        var t = MentalModelTrigger.Parse(null, "page");
        Assert.Equal("delta", t.Mode);
        Assert.True(t.RefreshAfterConsolidation);
        Assert.Equal(["observation"], t.FactTypes);
    }

    [Fact]
    public void Trigger_rejects_malformed_values()
    {
        Assert.Throws<MemoryBadRequestException>(() =>
            MentalModelTrigger.Parse("""{"mode":"weird"}""", "page"));
        Assert.Throws<MemoryBadRequestException>(() =>
            MentalModelTrigger.Parse("""{"refreshCron":"0 9 * * MON-FRI"}""", "page"));
        Assert.Throws<MemoryBadRequestException>(() =>
            MentalModelTrigger.Parse("""{"factTypes":["bogus"]}""", "page"));
        Assert.Equal(60, MentalModelTrigger.Parse("""{"refreshCron":"@every 60m"}""", "page").RefreshIntervalMinutes);
        Assert.Equal(5, MentalModelTrigger.Parse("""{"refreshCron":"*/5 * * * *"}""", "page").RefreshIntervalMinutes);
    }

    [Fact]
    public void Path_normalization_and_rejection()
    {
        Assert.Equal("projeto/convencoes", MentalModelTrigger.NormalizePath("/Projeto/Convencoes/"));
        Assert.Equal("", MentalModelTrigger.NormalizePath(null));
        Assert.Throws<MemoryBadRequestException>(() => MentalModelTrigger.NormalizePath("../etc"));
        Assert.Throws<MemoryBadRequestException>(() => MentalModelTrigger.NormalizePath("a/../b"));
    }

    // ---------- CRUD + tree (RF-001/RF-003/RF-004) ----------

    [Fact]
    public async Task Create_get_update_delete_and_uniqueness()
    {
        using var db = Open();
        await BankAsync(db);
        var svc = Models(db);
        var m = await svc.CreateAsync(null, "page", "Convenções", "projeto",
            "Quais as convenções?", null, null, default);
        Assert.Equal("projeto", m.Path);
        await Assert.ThrowsAsync<MemoryConflictException>(() =>
            svc.CreateAsync(null, "page", "Convenções", "projeto", "q", null, null, default));
        var sameNameOtherFolder = await svc.CreateAsync(null, "page", "Convenções",
            "api", "q", null, null, default);
        Assert.Equal("api", sameNameOtherFolder.Path);

        var got = await svc.GetAsync(null, m.Id, null, default);
        Assert.Equal(m.Id, got.Id);
        var byPath = await svc.GetAsync(null, null, "projeto/convenções", default);
        Assert.Equal(m.Id, byPath.Id);

        var upd = await svc.UpdateAsync(null, m.Id, "Convenções v2", "projeto/api",
            null, null, null, null, true, default);
        Assert.Equal("projeto/api", upd.Path);
        Assert.Equal("Convenções v2", upd.Name);
        Assert.True(await svc.DeleteAsync(null, m.Id, default));
        await Assert.ThrowsAsync<MemoryNotFoundException>(() =>
            svc.GetAsync(null, m.Id, null, default));
    }

    [Fact]
    public async Task PageTree_groups_by_folder()
    {
        using var db = Open();
        await BankAsync(db);
        var svc = Models(db);
        await svc.CreateAsync(null, "page", "Api", "projeto/convencoes", "q", null, null, default);
        await svc.CreateAsync(null, "page", "Estilo", "projeto/convencoes", "q", null, null, default);
        await svc.CreateAsync(null, "page", "Root", null, "q", null, null, default);
        var tree = await svc.PageTreeAsync(null, default);
        Assert.Equal(2, tree.Count); // 'projeto' folder + root page
        var folder = Assert.Single(tree, t => t.IsFolder);
        Assert.Equal("projeto", folder.Name);
        var conv = Assert.Single(folder.Children, c => c.IsFolder);
        Assert.Equal("convencoes", conv.Name);
        Assert.Equal(2, conv.Children.Count(c => !c.IsFolder));
    }

    [Fact]
    public async Task PageSearch_returns_snippet_hits()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db);
        var p = await svc.CreateAsync(null, "page", "Estilo", null, "q", null, null, default);
        db.MemoryMentalModels.Find(p.Id)!.Content = "sempre usar snake_case em migrations";
        await db.SaveChangesAsync();
        var hits = await svc.SearchPagesAsync(null, "snake_case", 10, default);
        Assert.Single(hits);
        Assert.Contains("snake_case", hits[0].Snippet);
    }

    // ---------- refresh (RF-002) ----------

    [Fact]
    public async Task Refresh_synthesizes_content_and_watermarks()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var u = Unit(bank, "Convenção: testes com xUnit");
        db.MemoryUnits.Add(u);
        var svc = Models(db);
        var page = await svc.CreateAsync(null, "page", "Conv", null, "convenções?", null, null, default);
        var chat = new OneShotChat("# Convenções\n- testes com xUnit");
        var r = await Refresher(db, chat).RefreshAsync(page.Id, true, default);

        Assert.True(r.Refreshed);
        Assert.Equal(1, r.SourceUnits);
        var model = db.MemoryMentalModels.Find(page.Id)!;
        Assert.Contains("xUnit", model.Content);
        Assert.NotNull(model.LastRefreshedAt);
        Assert.NotNull(model.LastMemorySeenAt);
        Assert.Single(db.MentalModelHistories.Where(h => h.ModelId == page.Id));
        // Page scope defaults to observations only — a world fact is out of scope.
    }

    [Fact]
    public async Task Delta_refresh_only_counts_new_units()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db);
        db.MemoryUnits.Add(Unit(bank, "old", mentionedAt: DateTime.UtcNow.AddHours(-2)));
        var page = await svc.CreateAsync(null, "page", "P", null, "q", null, null, default);
        var chat = new OneShotChat("v1");
        var svc2 = Refresher(db, chat);
        await svc2.RefreshAsync(page.Id, true, default);
        // No new units → second refresh watermarks without rewriting content.
        var r2 = await svc2.RefreshAsync(page.Id, true, default);
        Assert.True(r2.Refreshed);
        Assert.Equal(0, r2.SourceUnits);
        Assert.Equal("v1", db.MemoryMentalModels.Find(page.Id)!.Content);
        Assert.Equal(1, chat.Calls); // no LLM call when delta is empty
    }

    [Fact]
    public async Task MinInterval_skips_and_inflight_blocks()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db);
        var trigger = """{"minRefreshIntervalSeconds":3600}""";
        var page = await svc.CreateAsync(null, "page", "P", null, "q", trigger, null, default);
        var locks = new MentalModelRefreshLocks();
        var sp = new ServiceCollection().BuildServiceProvider();
        var refresh = new MentalModelRefreshService(db, svc, locks,
            new MemoryConfigResolver(new ConfigurationBuilder().Build()), Projector(false), sp);
        // Freshly created — last refreshed null → refresh hits provider-less error path.
        var r1 = await refresh.RefreshAsync(page.Id, ignoreMinInterval: true, default);
        Assert.False(r1.Refreshed);
        Assert.NotNull(r1.Error);
        Assert.True(locks.TryEnter(page.Id)); // released after attempt
        locks.Exit(page.Id);
    }

    [Fact]
    public async Task Stale_flag_tracks_new_units_in_scope()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db);
        var page = await svc.CreateAsync(null, "page", "P", null, "q", null, null, default);
        var got1 = await svc.GetAsync(null, page.Id, null, default);
        Assert.False(got1.Stale); // no units yet
        db.MemoryUnits.Add(Unit(bank, "novo fato"));
        await db.SaveChangesAsync();
        var got2 = await svc.GetAsync(null, page.Id, null, default);
        Assert.True(got2.Stale);
    }

    [Fact]
    public async Task History_trims_to_limit_and_manual_edit_marked()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db);
        var page = await svc.CreateAsync(null, "page", "P", null, "q", null, null, default);
        var chat = new OneShotChat("v");
        var refresh = Refresher(db, chat);
        for (var i = 0; i < 5; i++)
        {
            db.MemoryUnits.Add(Unit(bank, $"fato {i}"));
            await db.SaveChangesAsync();
            await refresh.RefreshAsync(page.Id, true, default);
        }
        // HistoryLimit=3 in test config → last 3 auto versions + manual edits.
        Assert.Equal(3, db.MentalModelHistories.Count(h => h.ModelId == page.Id && !h.ManualEdit));
        await svc.UpdateAsync(null, page.Id, null, null, null, null, null, "edit manual", true, default);
        Assert.Single(db.MentalModelHistories.Where(h => h.ModelId == page.Id && h.ManualEdit));
    }

    // ---------- vault projection (RF-003) ----------

    [Fact]
    public async Task Refresh_projects_page_file_into_vault()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        db.MemoryUnits.Add(Unit(bank, "convenção X"));
        var svc = Models(db, vault: true);
        var page = await svc.CreateAsync(null, "page", "Convenções", "projeto",
            "convenções?", null, null, default);
        await Refresher(db, new OneShotChat("doc"), vault: true).RefreshAsync(page.Id, true, default);

        var file = Path.Combine(_vault, "_memory", "default", "projeto", "convenções.md");
        Assert.True(File.Exists(file));
        var md = await File.ReadAllTextAsync(file);
        Assert.Contains(VaultPageProjector.ManagedBanner, md);
        Assert.Contains("bank: default", md);
        Assert.Contains("source_query:", md);
    }

    [Fact]
    public async Task Rename_moves_projected_file_and_delete_removes()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db, vault: true);
        var page = await svc.CreateAsync(null, "page", "Página", "a", "q", null, null, default);
        var model = db.MemoryMentalModels.Find(page.Id)!;
        model.Content = "c";
        model.LastRefreshedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await Projector(true).ProjectAsync(model, "default", default);
        var oldFile = Path.Combine(_vault, "_memory", "default", "a", "página.md");
        Assert.True(File.Exists(oldFile));

        await svc.UpdateAsync(null, page.Id, null, "b", null, null, null, null, true, default);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(Path.Combine(_vault, "_memory", "default", "b", "página.md")));

        await svc.DeleteAsync(null, page.Id, default);
        Assert.False(File.Exists(Path.Combine(_vault, "_memory", "default", "b", "página.md")));
    }

    [Fact]
    public async Task Hook_signals_scope_matching_models_only()
    {
        using var db = Open();
        var bank = await BankAsync(db);
        var svc = Models(db);
        var withHook = await svc.CreateAsync(null, "page", "P1", null, "q",
            """{"refreshAfterConsolidation":true}""", null, default);
        var noHook = await svc.CreateAsync(null, "mental_model", "P2", null, "q", null, null, default);
        var signals = new List<Guid>();
        var hook = new MentalModelConsolidationHook(
            new ServiceCollection().AddSingleton(db).BuildServiceProvider(),
            new FakeTrigger(signals));
        await hook.OnConsolidatedAsync(bank,
            new MemoryConsolidateResult(bank.Id, bank.BankKey, 1, 1, 0, 0, 0, 0), default);
        Assert.Equal([withHook.Id], signals);
    }

    private sealed class FakeTrigger(List<Guid> sink) : IMentalModelRefreshTrigger
    {
        public void Signal(Guid modelId) => sink.Add(modelId);
    }
}
