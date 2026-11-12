using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using KnowledgeHub.Server.Memory.MentalModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261112-memory-context-timeline-stats: session-start digest (RF-001),
/// chronological buckets (RF-002) and per-bank stats (RF-003) — deterministic,
/// no LLM by default, bank-scoped.
/// </summary>
public class MemoryContextTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-memctx-{Guid.NewGuid():N}.db");

    public MemoryContextTests()
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

    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:test";
        public int Dimensions => 8;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new float[Dimensions]);
        public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default) =>
            EmbedAsync(text, ct);
    }

    private sealed class FakeScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private readonly Queue<string> _replies = new(replies);
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, _replies.Count > 0 ? _replies.Dequeue() : "summary")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private async Task<(MemoryContextService Ctx, MemoryRetainService Retain, MemoryBank Bank)> SetupAsync(
        KnowledgeHubDbContext db, IChatClient? chat = null)
    {
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var bank = await banks.ResolveAsync(null, default);
        var sp = new ServiceCollection();
        if (chat is not null)
            sp.AddSingleton(chat);
        var provider = sp.BuildServiceProvider();
        var models = new MentalModelService(db, banks,
            new VaultPageProjector(Cfg(), NullLogger<VaultPageProjector>.Instance), provider);
        var retain = new MemoryRetainService(db, new MemoryRetainDeps(banks,
            new MemoryConfigResolver(Cfg()),
            new MemoryEntityLinker(db), new NoOpMemoryDefenseScreen(),
            provider, new FakeEmbeddings(), new MemoryConsolidationTrigger()),
            NullLogger<MemoryRetainService>.Instance);
        return (new MemoryContextService(db, banks, models, provider), retain, bank);
    }

    private static IConfiguration Cfg(Dictionary<string, string?>? extra = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(extra ?? []).Build();

    private static async Task SeedDirectivesAsync(KnowledgeHubDbContext db, MemoryBank bank)
    {
        db.MemoryDirectives.AddRange(
            new MemoryDirective { BankId = bank.Id, Name = "pt-BR", Content = "responda em pt-BR", Priority = 10 },
            new MemoryDirective { BankId = bank.Id, Name = "low", Content = "low prio", Priority = 1 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RF001_context_digest_fills_all_sections()
    {
        // AC: bank com 2 directives, 5 fatos, 1 observation, 1 model stale, 1 operação falha
        // → digest traz as 5 seções preenchidas, sem chamada LLM.
        using var db = Open();
        var (ctx, retain, bank) = await SetupAsync(db);
        await SeedDirectivesAsync(db, bank);
        for (var i = 0; i < 5; i++)
            await retain.RetainAsync(null, new MemoryRetainRequest($"fato número {i}", Strategy: "verbatim"), default);
        db.MemoryUnits.Add(new MemoryUnit
        {
            BankId = bank.Id,
            Text = "observação consolidada",
            FactType = "observation",
            ContentHash = "h-obs",
            ProofCount = 3
        });
        db.MemoryMentalModels.Add(new MemoryMentalModel
        {
            Id = Guid.NewGuid(),
            BankId = bank.Id,
            Subtype = "mental_model",
            Name = "deploy-model",
            SourceQuery = "deploy",
            LastMemorySeenAt = DateTime.UtcNow.AddDays(-2)
        });
        db.MemoryOperations.Add(new MemoryOperation
        {
            BankId = bank.Id,
            Kind = "retain",
            Status = "failed",
            PayloadJson = "{}",
            Error = "llm offline"
        });
        await db.SaveChangesAsync();

        var digest = await ctx.ContextAsync(null, 10, null, false, default);

        Assert.Equal(2, digest.Directives.Count);
        Assert.Equal("pt-BR", digest.Directives[0].Name);
        Assert.Equal(5, digest.RecentFacts.Count); // observations live in their own section
        Assert.Single(digest.TopObservations);
        Assert.Single(digest.StaleModels);
        Assert.Single(digest.RecentFailures);
        Assert.Equal("retain", digest.RecentFailures[0].Kind);
        Assert.False(digest.Synthesized);
        Assert.Null(digest.SynthesizedText);

        var text = MemoryContextService.BuildDigest(digest);
        Assert.Contains("# Memory context", text);
        Assert.Contains("## Directives", text);
        Assert.Contains("## Recent facts", text);
        Assert.Contains("## Stale models", text);
        Assert.Contains("## Recent operation failures", text);
    }

    [Fact]
    public async Task RF001_synthesize_uses_chat_when_configured()
    {
        // AC: synthesize:true → digest vai ao IChatClient; sem provider → flag false.
        using var db = Open();
        var (noChat, retain, _) = await SetupAsync(db);
        await retain.RetainAsync(null, new MemoryRetainRequest("fato", Strategy: "verbatim"), default);

        var off = await noChat.ContextAsync(null, 10, null, synthesize: true, default);
        Assert.False(off.Synthesized);

        using var db2 = Open();
        var (withChat, _, _) = await SetupAsync(db2, new ScriptedChat("resumo da sessão"));
        var on = await withChat.ContextAsync(null, 10, null, synthesize: true, default);
        Assert.True(on.Synthesized);
        Assert.Equal("resumo da sessão", on.SynthesizedText);
    }

    [Fact]
    public async Task RF001_empty_bank_returns_empty_sections()
    {
        // Edge: bank vazio → seções vazias, stats.totalUnits=0, sem erro.
        using var db = Open();
        var (ctx, _, _) = await SetupAsync(db);
        var digest = await ctx.ContextAsync(null, 10, null, false, default);
        Assert.Equal(0, digest.Stats.TotalUnits);
        Assert.Empty(digest.RecentFacts);
        Assert.Empty(digest.Directives);
    }

    [Fact]
    public async Task RF002_timeline_groups_by_day_and_falls_back_to_mentioned()
    {
        // AC: OccurredStart em 3 dias distintos → 3 buckets ordenados;
        // fato sem OccurredStart cai no bucket de MentionedAt.
        using var db = Open();
        var (ctx, _, bank) = await SetupAsync(db);
        var day1 = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);
        db.MemoryUnits.AddRange(
            new MemoryUnit { BankId = bank.Id, Text = "d1", FactType = "world", ContentHash = "h1", OccurredStart = day1, MentionedAt = day1 },
            new MemoryUnit { BankId = bank.Id, Text = "d2", FactType = "world", ContentHash = "h2", OccurredStart = day2, MentionedAt = day2 },
            new MemoryUnit { BankId = bank.Id, Text = "d3", FactType = "world", ContentHash = "h3", OccurredStart = day3, MentionedAt = day3 },
            new MemoryUnit { BankId = bank.Id, Text = "no-occ", FactType = "world", ContentHash = "h4", MentionedAt = day2 });
        await db.SaveChangesAsync();

        var result = await ctx.TimelineAsync(null, null, null, null, "day", 50, default);

        Assert.Equal(3, result.Buckets.Count);
        Assert.Equal("2026-11-10", result.Buckets[0].Bucket); // newest first
        Assert.Equal("2026-11-09", result.Buckets[1].Bucket);
        Assert.Equal(2, result.Buckets[1].Count); // d2 + no-occ (MentionedAt fallback)
        Assert.Equal("2026-11-08", result.Buckets[2].Bucket);
    }

    [Fact]
    public async Task RF002_timeline_facttype_filter_and_from_to()
    {
        // AC: factType=observation → só observations; from>to → 400.
        using var db = Open();
        var (ctx, _, bank) = await SetupAsync(db);
        db.MemoryUnits.AddRange(
            new MemoryUnit { BankId = bank.Id, Text = "fato", FactType = "world", ContentHash = "h1" },
            new MemoryUnit { BankId = bank.Id, Text = "obs", FactType = "observation", ContentHash = "h2", ProofCount = 2 });
        await db.SaveChangesAsync();

        var obsOnly = await ctx.TimelineAsync(null, null, null, ["observation"], "day", 50, default);
        Assert.All(obsOnly.Buckets.SelectMany(b => b.Items), i => Assert.Equal("observation", i.FactType));

        await Assert.ThrowsAsync<MemoryBadRequestException>(() =>
            ctx.TimelineAsync(null, DateTime.UtcNow, DateTime.UtcNow.AddDays(-1), null, "day", 50, default));
    }

    [Fact]
    public async Task RF003_stats_reports_counts_and_stale()
    {
        // AC: 3 world + 2 experience + 2 observations (proof 3 e 1)
        // → unitsByFactType, avgProofCount=2, stale correto.
        using var db = Open();
        var (ctx, _, bank) = await SetupAsync(db);
        for (var i = 0; i < 3; i++)
            db.MemoryUnits.Add(new MemoryUnit { BankId = bank.Id, Text = $"w{i}", FactType = "world", ContentHash = $"w{i}" });
        for (var i = 0; i < 2; i++)
            db.MemoryUnits.Add(new MemoryUnit { BankId = bank.Id, Text = $"e{i}", FactType = "experience", ContentHash = $"e{i}" });
        db.MemoryUnits.Add(new MemoryUnit { BankId = bank.Id, Text = "o1", FactType = "observation", ContentHash = "o1", ProofCount = 3 });
        db.MemoryUnits.Add(new MemoryUnit { BankId = bank.Id, Text = "o2", FactType = "observation", ContentHash = "o2", ProofCount = 1 });
        db.MemoryMentalModels.Add(new MemoryMentalModel
        {
            Id = Guid.NewGuid(),
            BankId = bank.Id,
            Subtype = "mental_model",
            Name = "m1",
            SourceQuery = "x",
            LastMemorySeenAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var stats = Assert.IsType<MemoryContextService.MemoryStatsResult>(
            await ctx.StatsAsync("default", default));

        Assert.Equal(7, stats.TotalUnits);
        Assert.Equal(3, stats.UnitsByFactType["world"]);
        Assert.Equal(2, stats.UnitsByFactType["experience"]);
        Assert.Equal(2, stats.UnitsByFactType["observation"]);
        Assert.Equal(2, stats.Observations.Total);
        Assert.Equal(2.0, stats.Observations.AvgProofCount);
        Assert.Equal(3, stats.Observations.MaxProofCount);
        Assert.Equal(1, stats.MentalModels.Total);
        Assert.Equal(1, stats.MentalModels.Stale);
        Assert.NotNull(stats.OldestUnconsolidatedFact);
    }

    [Fact]
    public async Task RF003_stats_null_bankkey_aggregates_scope()
    {
        // bankKey ausente → agregado de todos os banks do escopo.
        using var db = Open();
        var (ctx, _, bank) = await SetupAsync(db);
        db.MemoryUnits.Add(new MemoryUnit { BankId = bank.Id, Text = "u", FactType = "world", ContentHash = "u" });
        await db.SaveChangesAsync();

        var global = Assert.IsType<MemoryContextService.GlobalStatsResult>(
            await ctx.StatsAsync(null, default));
        Assert.Equal(1, global.TotalBanks);
        Assert.Equal(1, global.TotalUnits);
    }
}
