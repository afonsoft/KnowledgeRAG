using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261111-memory-observations-consolidation: batch consolidation LLM
/// (create/update/delete/skip), evidence rule, refine-not-overwrite +
/// ObservationHistory, dedup adjudication, preferObservations, per-bank lock,
/// backoff on failure, worker scan.
/// </summary>
public class MemoryConsolidationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-con-{Guid.NewGuid():N}.db");

    public MemoryConsolidationTests()
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

    /// <summary>Bag-of-words vectors — similar texts stay similar.</summary>
    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:test";
        public int Dimensions => 8;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[Dimensions];
            foreach (var t in MemoryRecallService.Tokenize(text))
                v[Math.Abs(t.GetHashCode()) % Dimensions] += 1;
            return Task.FromResult(v);
        }
    }

    /// <summary>Queued scripted responses — deterministic LLM stand-in.</summary>
    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private readonly Queue<string> _replies = new(replies);
        public List<string> Prompts { get; } = [];
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(messages.Last().Text ?? "");
            var reply = _replies.Count > 0 ? _replies.Dequeue() : "[]";
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static IConfiguration Cfg(Dictionary<string, string?>? extra = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(extra ?? []).Build();

    private static ServiceProvider Sp(IChatClient? chat)
    {
        var sc = new ServiceCollection();
        if (chat is not null) sc.AddSingleton(chat);
        return sc.BuildServiceProvider();
    }

    private MemoryConsolidationService Service(KnowledgeHubDbContext db, IChatClient? chat,
        MemoryConsolidationLocks? locks = null, Dictionary<string, string?>? cfg = null,
        IEnumerable<IMemoryConsolidationHook>? hooks = null) =>
        new(db, new MemoryConfigResolver(Cfg(cfg)), locks ?? new MemoryConsolidationLocks(),
            Sp(chat), new FakeEmbeddings(),
            hooks ?? [new NoOpMemoryConsolidationHook()],
            NullLogger<MemoryConsolidationService>.Instance);

    private async Task<MemoryBank> SeedAsync(KnowledgeHubDbContext db, params string[] facts)
    {
        var bank = new MemoryBank
        { BankKey = "default", ApiKeyId = MemoryBank.ServerWideKey, Name = "default" };
        db.MemoryBanks.Add(bank);
        foreach (var f in facts)
            db.MemoryUnits.Add(new MemoryUnit
            {
                BankId = bank.Id,
                Text = f,
                FactType = "world",
                ContentHash = MemoryHasher.Hash(f)
            });
        await db.SaveChangesAsync();
        return bank;
    }

    [Fact]
    public async Task AC1_create_observation_with_evidence_proof_and_history()
    {
        using var db = Open();
        var bank = await SeedAsync(db,
            "Alice é engenheira", "Alice é engenheira sênior", "Alice foi promovida a staff");
        var chat = new ScriptedChat("""
            [{"action":"create","fact_ids":["f1","f2","f3"],
              "text":"Alice é staff engineer (progressão: engenheira → sênior → staff)",
              "evidence":["Alice é engenheira","promovida a staff"]}]
            """);
        var result = await Service(db, chat).RunAsync(bank, default);

        Assert.Equal(3, result.Processed);
        Assert.Equal(1, result.Created);
        var obs = await db.MemoryUnits.SingleAsync(u => u.FactType == "observation");
        Assert.Equal(3, obs.ProofCount);
        Assert.Equal(3, MemoryRecallService.ParseSources(obs.SourceMemoryIdsJson).Count);
        Assert.NotNull(obs.Embedding);
        Assert.Equal(3, await db.MemoryUnits.CountAsync(u =>
            u.FactType == "world" && u.ConsolidatedAt != null));
        var history = await db.ObservationHistories.SingleAsync();
        Assert.Equal(1, history.Version);
        Assert.Equal("create", history.Action);
    }

    [Fact]
    public async Task AC2_update_refines_text_and_history_keeps_versions()
    {
        using var db = Open();
        var bank = new MemoryBank
        { BankKey = "default", ApiKeyId = MemoryBank.ServerWideKey, Name = "d" };
        db.MemoryBanks.Add(bank);
        var src = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "v1 fato",
            FactType = "world",
            ContentHash = "h1",
            ConsolidatedAt = DateTime.UtcNow
        };
        var obs = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "Alice é engenheira",
            FactType = "observation",
            ContentHash = MemoryHasher.Hash("Alice é engenheira"),
            SourceMemoryIdsJson = JsonSerializer.Serialize(new[] { src.Id }),
            ProofCount = 1
        };
        var newFact = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "Alice foi promovida a staff",
            FactType = "world",
            ContentHash = "h3"
        };
        db.MemoryUnits.AddRange(src, obs, newFact);
        db.ObservationHistories.Add(new ObservationHistory
        {
            ObservationUnitId = obs.Id,
            BankId = bank.Id,
            Version = 1,
            Text = "Alice é engenheira",
            AddedSourceIdsJson = "[]",
            ProofCount = 1,
            Action = "create"
        });
        await db.SaveChangesAsync();

        var chat = new ScriptedChat("""
            [{"action":"update","observation_id":"o1","fact_ids":["f1"],
              "text":"Alice é staff engineer"}]
            """);
        var result = await Service(db, chat).RunAsync(bank, default);

        Assert.Equal(1, result.Updated);
        var updated = await db.MemoryUnits.FindAsync(obs.Id);
        Assert.Equal("Alice é staff engineer", updated!.Text);
        Assert.Equal(2, updated.ProofCount);
        Assert.Equal(2, MemoryRecallService.ParseSources(updated.SourceMemoryIdsJson).Count);
        Assert.NotNull(newFact.ConsolidatedAt);
        var versions = await db.ObservationHistories
            .Where(h => h.ObservationUnitId == obs.Id).OrderBy(h => h.Version).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal("Alice é engenheira", versions[0].Text);   // previous version preserved
        Assert.Equal("Alice é staff engineer", versions[1].Text); // refined version appended
        Assert.Equal("update", versions[1].Action);
    }

    [Fact]
    public async Task AC3_prefer_observations_suppresses_source_facts_in_recall()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "O deploy falhou por falta de memória");
        var emb = new FakeEmbeddings();
        var unit = await db.MemoryUnits.FirstAsync(u => u.FactType == "world");
        unit.Embedding = MemoryRetainService.ToBytes(await emb.EmbedAsync(unit.Text));
        var obs = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "O deploy falhou por falta de memória no nó",
            FactType = "observation",
            ContentHash = "oh",
            SourceMemoryIdsJson = JsonSerializer.Serialize(new[] { unit.Id }),
            ProofCount = 1,
            Embedding = MemoryRetainService.ToBytes(
                await emb.EmbedAsync("O deploy falhou por falta de memória no nó")),
            EmbeddingModel = emb.ModelId
        };
        db.MemoryUnits.Add(obs);
        await db.SaveChangesAsync();

        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var recall = new MemoryRecallService(db, banks, emb,
            NullLogger<MemoryRecallService>.Instance);
        var result = await recall.RecallAsync(null,
            new MemoryRecallRequest("deploy memória", PreferObservations: true), default);

        Assert.Contains(result.Results, r => r.FactType == "observation" && r.ProofCount == 1);
        Assert.DoesNotContain(result.Results, r => r.UnitId == unit.Id); // source suppressed
        var withSources = await recall.RecallAsync(null, new MemoryRecallRequest(
            "deploy memória", PreferObservations: true, IncludeSourceFacts: true), default);
        Assert.Equal(unit.Text, withSources.Results
            .First(r => r.FactType == "observation").SourceFacts![0].Text);
    }

    [Fact]
    public async Task AC4_llm_failure_marks_failed_and_retries_after_backoff()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "fato qualquer");
        var badChat = new ScriptedChat("not json at all");
        var cfg = new Dictionary<string, string?>
        { ["Memory:Consolidation:RetryBackoffMinutes"] = "-1" }; // no backoff → retry next tick
        var first = await Service(db, badChat, cfg: cfg).RunAsync(bank, default);
        Assert.Equal(1, first.Failed);
        var unit = await db.MemoryUnits.SingleAsync(u => u.FactType == "world");
        Assert.Null(unit.ConsolidatedAt);
        Assert.NotNull(unit.ConsolidationFailedAt);
        Assert.Equal(0, await db.ObservationHistories.CountAsync()); // nothing partial

        var goodChat = new ScriptedChat("""
            [{"action":"create","fact_ids":["f1"],"text":"obs sobre o fato",
              "evidence":["fato qualquer"]}]
            """);
        var second = await Service(db, goodChat, cfg: cfg).RunAsync(bank, default);
        Assert.Equal(1, second.Created);
        Assert.NotNull(await db.MemoryUnits.SingleAsync(u =>
            u.FactType == "world").ContinueWith(t => t.Result.ConsolidatedAt));
    }

    [Fact]
    public async Task AC5_dedup_adjudication_folds_into_existing_observation()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "Alice é engenheira");
        // Run 1: creates the observation.
        var chat1 = new ScriptedChat("""
            [{"action":"create","fact_ids":["f1"],"text":"Alice é engenheira",
              "evidence":["Alice é engenheira"]}]
            """);
        await Service(db, chat1).RunAsync(bank, default);

        // Retain a near-identical fact (different hash).
        db.MemoryUnits.Add(new MemoryUnit
        {
            BankId = bank.Id,
            Text = "Alice trabalha como engenheira de software",
            FactType = "world",
            ContentHash = "different-hash"
        });
        await db.SaveChangesAsync();

        // Run 2: adjudication says "same" → folds, no new observation.
        var chat2 = new ScriptedChat("""[{"pair":"p1","decision":"same"}]""");
        var cfg = new Dictionary<string, string?>
        { ["Memory:Consolidation:DedupThreshold"] = "0.0" }; // always adjudicate
        var result = await Service(db, chat2, cfg: cfg).RunAsync(bank, default);

        Assert.Equal(1, result.Updated);
        var obs = await db.MemoryUnits.SingleAsync(u => u.FactType == "observation");
        Assert.Equal(2, obs.ProofCount); // folded — one observation, proof 2
        Assert.Equal(1, await db.MemoryUnits.CountAsync(u => u.FactType == "observation"));
    }

    [Fact]
    public async Task RF001_second_run_for_same_bank_conflicts()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "fato");
        var locks = new MemoryConsolidationLocks();
        Assert.True(locks.TryAcquire(bank.Id)); // simulate an in-flight run
        var ex = await Assert.ThrowsAsync<MemoryConflictException>(() =>
            Service(db, new ScriptedChat(), locks).RunAsync(bank, default));
        Assert.Contains("already in progress", ex.Message);
    }

    [Fact]
    public async Task RF001_consolidate_requires_chat_provider()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "fato");
        await Assert.ThrowsAsync<MemoryServiceUnavailableException>(() =>
            Service(db, chat: null).RunAsync(bank, default));
    }

    [Fact]
    public async Task RF003_history_endpoint_returns_versions_in_order()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "Alice é engenheira");
        var chat = new ScriptedChat("""
            [{"action":"create","fact_ids":["f1"],"text":"Alice é engenheira",
              "evidence":["Alice é engenheira"]}]
            """);
        var svc = Service(db, chat);
        await svc.RunAsync(bank, default);
        var obs = await db.MemoryUnits.SingleAsync(u => u.FactType == "observation");

        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var history = await svc.HistoryAsync(null, obs.Id, banks, default);
        Assert.Single(history);
        Assert.Equal("create", history[0].Action);
        Assert.Equal(1, history[0].Version);

        await Assert.ThrowsAsync<MemoryNotFoundException>(() =>
            svc.HistoryAsync(null, Guid.NewGuid(), banks, default));
    }

    [Fact]
    public async Task RF001_worker_scan_consolidates_pending_banks()
    {
        // Seed pending facts through a real DI scope factory (worker path).
        var services = new ServiceCollection()
            .AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"))
            .AddSingleton(new MemoryConsolidationLocks())
            .AddSingleton<IChatClient>(new ScriptedChat("""
                [{"action":"create","fact_ids":["f1"],"text":"obs do worker",
                  "evidence":["fato do worker"]}]
                """))
            .AddSingleton<IEmbeddingProvider>(new FakeEmbeddings())
            .AddSingleton<IMemoryConsolidationHook>(new NoOpMemoryConsolidationHook())
            .AddSingleton(new MemoryConfigResolver(
                Cfg(new Dictionary<string, string?>
                { ["Memory:Consolidation:IntervalMinutes"] = "1" })))
            .AddScoped<MemoryConsolidationService>()
            .AddLogging()
            .BuildServiceProvider();

        using var db = Open();
        await SeedAsync(db, "fato do worker");

        var worker = new MemoryConsolidationWorker(new MemoryConsolidationTrigger(),
            services.GetRequiredService<MemoryConfigResolver>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MemoryConsolidationWorker>.Instance);
        await worker.ScanAsync(default);

        using var verify = Open();
        Assert.Equal(1, await verify.MemoryUnits.CountAsync(u => u.FactType == "observation"));
    }

    [Fact]
    public async Task RF002_facts_unreferenced_by_llm_are_skipped_not_pending()
    {
        using var db = Open();
        var bank = await SeedAsync(db, "fato A", "fato B");
        // LLM only covers f1 — f2 must be skipped, not left pending.
        var chat = new ScriptedChat("""
            [{"action":"create","fact_ids":["f1"],"text":"obs de A",
              "evidence":["fato A"]}]
            """);
        var result = await Service(db, chat).RunAsync(bank, default);
        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Skipped);
        var units = await db.MemoryUnits.Where(u => u.FactType == "world").ToListAsync();
        Assert.All(units, u => Assert.NotNull(u.ConsolidatedAt));
    }

    [Fact]
    public async Task RF005_triage_folds_restate_and_discards_chatter()
    {
        using var db = Open();
        var bank = new MemoryBank
        { BankKey = "default", ApiKeyId = MemoryBank.ServerWideKey, Name = "d" };
        db.MemoryBanks.Add(bank);
        var obs = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "Alice é engenheira",
            FactType = "observation",
            ContentHash = "oh",
            SourceMemoryIdsJson = "[]",
            ProofCount = 0
        };
        db.MemoryUnits.AddRange(obs,
            new MemoryUnit
            {
                BankId = bank.Id,
                Text = "Alice é engenheira de software",
                FactType = "world",
                ContentHash = "f1h"
            },
            new MemoryUnit
            {
                BankId = bank.Id,
                Text = "haha lol",
                FactType = "experience",
                ContentHash = "f2h"
            });
        await db.SaveChangesAsync();

        // Triage verdicts — no decide call needed for these two facts.
        var chat = new ScriptedChat("""
            [{"fact_id":"f1","verdict":"restate","observation_id":"o1"},
             {"fact_id":"f2","verdict":"chatter"}]
            """);
        var cfg = new Dictionary<string, string?>
        { ["Memory:Consolidation:Triage"] = "decision-model" };
        var result = await Service(db, chat, cfg: cfg).RunAsync(bank, default);

        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Updated);  // restate folded
        Assert.Equal(1, result.Skipped);  // chatter stamped, no action
        Assert.Single(chat.Prompts); // triage only — decide was skipped
        Assert.Equal(1, (await db.MemoryUnits.FindAsync(obs.Id))!.ProofCount);
    }
}
