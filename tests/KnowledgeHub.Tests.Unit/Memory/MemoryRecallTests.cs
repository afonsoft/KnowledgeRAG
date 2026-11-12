using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-004: 4-arm recall — lexical/semantic
/// hits, graph arm via entity links, temporal arm gated on window, minScore,
/// budget, factType filter, bank isolation.
/// </summary>
public class MemoryRecallTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-memr-{Guid.NewGuid():N}.db");

    public MemoryRecallTests()
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
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[Dimensions];
            foreach (var t in MemoryRecallService.Tokenize(text))
                v[Math.Abs(t.GetHashCode()) % Dimensions] += 1;
            return Task.FromResult(v);
        }
    }

    private sealed class FakeScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private async Task<(MemoryRetainService Retain, MemoryRecallService Recall, MemoryBank Bank)> SetupAsync(KnowledgeHubDbContext db)
    {
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var bank = await banks.ResolveAsync(null, default);
        var retain = new MemoryRetainService(db, new MemoryRetainDeps(banks,
            new MemoryConfigResolver(new ConfigurationBuilder().Build()),
            new MemoryEntityLinker(db), new NoOpMemoryDefenseScreen(),
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            new FakeEmbeddings(), new MemoryConsolidationTrigger()),
            NullLogger<MemoryRetainService>.Instance);
        var recall = new MemoryRecallService(db, banks, new FakeEmbeddings(),
            NullLogger<MemoryRecallService>.Instance);
        return (retain, recall, bank);
    }

    [Fact]
    public async Task RF004_lexical_arm_finds_retained_fact()
    {
        using var db = Open();
        var (retain, recall, _) = await SetupAsync(db);
        await retain.RetainAsync(null, new MemoryRetainRequest(
            "O deploy de pagamentos falhou por falta de memória", Strategy: "verbatim"), default);

        var result = await recall.RecallAsync(null, new MemoryRecallRequest("falha no deploy de pagamentos"), default);
        Assert.NotEmpty(result.Results);
        Assert.Contains("lexical", result.ArmsUsed);
        Assert.Contains("deploy", result.Results[0].Text);
    }

    [Fact]
    public async Task RF004_temporal_arm_only_active_with_window()
    {
        using var db = Open();
        var (retain, recall, _) = await SetupAsync(db);
        await retain.RetainAsync(null, new MemoryRetainRequest(
            "fato recente sobre latência", Strategy: "verbatim"), default);

        var noWindow = await recall.RecallAsync(null, new MemoryRecallRequest("latência"), default);
        Assert.DoesNotContain("temporal", noWindow.ArmsUsed);

        var withWindow = await recall.RecallAsync(null,
            new MemoryRecallRequest("latência", Window: "7d"), default);
        Assert.Contains("temporal", withWindow.ArmsUsed);
    }

    [Fact]
    public async Task RF004_facttype_filter_restricts_results()
    {
        using var db = Open();
        var (retain, recall, _) = await SetupAsync(db);
        await retain.RetainAsync(null, new MemoryRetainRequest("experiência do agente", Strategy: "verbatim"), default);
        var worldOnly = await recall.RecallAsync(null,
            new MemoryRecallRequest("experiência", FactType: "world"), default);
        Assert.Empty(worldOnly.Results);
    }

    [Fact]
    public async Task RF004_recall_is_bank_isolated()
    {
        using var db = Open();
        var (retain, _, _) = await SetupAsync(db);
        await retain.RetainAsync(null, new MemoryRetainRequest("segredo do bank default", Strategy: "verbatim"), default);

        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var other = await banks.CreateAsync("outro", "Outro", null, null, null, default);
        var recall = new MemoryRecallService(db, banks, new FakeEmbeddings(),
            NullLogger<MemoryRecallService>.Instance);
        var result = await recall.RecallAsync("outro", new MemoryRecallRequest("segredo"), default);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task RF004_graph_arm_ranks_entity_linked_units()
    {
        using var db = Open();
        var (retain, recall, bank) = await SetupAsync(db);
        await retain.RetainAsync(null, new MemoryRetainRequest("Postgres caiu", Strategy: "verbatim"), default);
        // Simulate what extract mode does: link the unit to a KgNode.
        var unit = await db.MemoryUnits.FirstAsync();
        var node = new KgNode { Name = "Postgres", NormalizedName = "postgres", Type = "database" };
        db.KgNodes.Add(node);
        db.MemoryUnitEntities.Add(new MemoryUnitEntity { UnitId = unit.Id, EntityId = node.Id, BankId = bank.Id });
        await db.SaveChangesAsync(default);

        var result = await recall.RecallAsync(null, new MemoryRecallRequest("postgres"), default);
        Assert.Contains("graph", result.ArmsUsed);
        Assert.Equal(unit.Id, result.Results[0].UnitId);
        Assert.Contains("Postgres", result.Results[0].Entities);
    }

    [Fact]
    public async Task RF004_invalid_window_is_a_caller_error()
    {
        using var db = Open();
        var (_, recall, _) = await SetupAsync(db);
        await Assert.ThrowsAsync<MemoryBadRequestException>(() =>
            recall.RecallAsync(null, new MemoryRecallRequest("x", Window: "janela-inválida"), default));
    }
}
