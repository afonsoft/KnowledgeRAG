using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-001/RF-002/RF-005: hashing, chunking,
/// verbatim retain, dedupe, delta-retain, bank isolation, guardrails.
/// </summary>
public class MemoryRetainTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-mem-{Guid.NewGuid():N}.db");

    public MemoryRetainTests()
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

    /// <summary>Deterministic bag-of-words vectors — similar texts stay similar.</summary>
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

    private MemoryRetainService Retain(KnowledgeHubDbContext db, IServiceProvider? sp = null, CallerScope? scope = null)
    {
        var services = new ServiceCollection();
        if (sp is null)
        {
            // No IChatClient registered → extract mode must fail clearly.
        }
        var banks = new MemoryBankService(db, new FakeScope(scope ?? CallerScope.Unrestricted));
        return new MemoryRetainService(db, new MemoryRetainDeps(banks,
            new MemoryConfigResolver(new ConfigurationBuilder().Build()),
            new MemoryEntityLinker(db), new NoOpMemoryDefenseScreen(),
            sp ?? services.BuildServiceProvider(),
            new FakeEmbeddings(), new MemoryConsolidationTrigger()),
            NullLogger<MemoryRetainService>.Instance);
    }

    [Fact]
    public async Task RF001_verbatim_retain_creates_units_with_hash()
    {
        using var db = Open();
        var result = await Retain(db).RetainAsync(null,
            new MemoryRetainRequest("O deploy falhou no nó 3.", Strategy: "verbatim"), default);
        Assert.Equal(1, result.UnitsCreated);
        var unit = await db.MemoryUnits.SingleAsync();
        Assert.Equal("O deploy falhou no nó 3.", unit.Text);
        Assert.Equal("experience", unit.FactType);
        Assert.Equal(64, unit.ContentHash.Length);
        Assert.NotNull(unit.Embedding);
    }

    [Fact]
    public async Task RF002_verbatim_dedupes_identical_content()
    {
        using var db = Open();
        var svc = Retain(db);
        await svc.RetainAsync(null, new MemoryRetainRequest("mesmo fato", Strategy: "verbatim"), default);
        var second = await svc.RetainAsync(null, new MemoryRetainRequest("mesmo fato", Strategy: "verbatim"), default);
        Assert.Equal(0, second.UnitsCreated);
        Assert.Equal(1, await db.MemoryUnits.CountAsync(u => !u.IsDeleted));
    }

    [Fact]
    public async Task RF005_delta_retain_preserves_unchanged_chunk_ids()
    {
        using var db = Open();
        var svc = Retain(db);
        var v1 = "Parágrafo inalterado sobre Postgres.\n\nBloco que vai mudar.";
        var first = await svc.RetainAsync(null,
            new MemoryRetainRequest(v1, Strategy: "verbatim", DocumentId: "doc-1"), default);
        Assert.Equal(2, first.UnitsCreated);

        var v2 = "Parágrafo inalterado sobre Postgres.\n\nBloco totalmente novo.";
        var second = await svc.RetainAsync(null,
            new MemoryRetainRequest(v2, Strategy: "verbatim", DocumentId: "doc-1"), default);
        Assert.Equal(1, second.UnitsCreated);
        Assert.Equal(1, second.UnitsPreserved);
        // The unchanged paragraph keeps its original unit id.
        Assert.Contains(first.UnitIds[0], second.UnitIds);
        Assert.Equal(1, await db.MemoryUnits.CountAsync(u => u.IsDeleted));
    }

    [Fact]
    public async Task RF001_banks_are_isolated_per_api_key_scope()
    {
        using var db = Open();
        var keyA = CallerScope.FromJson(Guid.NewGuid(), null, null);
        var keyB = CallerScope.FromJson(Guid.NewGuid(), null, null);
        await Retain(db, scope: keyA).RetainAsync(null,
            new MemoryRetainRequest("fato da chave A", Strategy: "verbatim"), default);

        var banksB = new MemoryBankService(db, new FakeScope(keyB));
        var resolve = await banksB.ResolveAsync(null, default); // auto-creates B's own 'default'
        Assert.NotEqual(resolve.Id, (await db.MemoryBanks.FirstAsync(b => b.BankKey == "default" && b.ApiKeyId != resolve.ApiKeyId)).Id);
        Assert.Equal(2, await db.MemoryBanks.CountAsync(b => b.BankKey == "default"));
        // B's default bank holds no units.
        Assert.Equal(0, await db.MemoryUnits.CountAsync(u => u.BankId == resolve.Id));
    }

    [Fact]
    public async Task RF002_extract_without_llm_fails_clearly_verbatim_works()
    {
        using var db = Open();
        var svc = Retain(db); // no IChatClient in the provider
        var ex = await Assert.ThrowsAsync<MemoryServiceUnavailableException>(() =>
            svc.RetainAsync(null, new MemoryRetainRequest("fato", Strategy: "extract"), default));
        Assert.Contains("verbatim", ex.Message);
        var ok = await svc.RetainAsync(null, new MemoryRetainRequest("fato", Strategy: "verbatim"), default);
        Assert.Equal(1, ok.UnitsCreated);
    }

    [Fact]
    public async Task RF002_unknown_bank_recall_fails_with_400()
    {
        using var db = Open();
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var recall = new MemoryRecallService(db, banks, new FakeEmbeddings(),
            NullLogger<MemoryRecallService>.Instance);
        var ex = await Assert.ThrowsAsync<MemoryNotFoundException>(() =>
            recall.RecallAsync("nao-existe", new MemoryRecallRequest("q"), default));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task RF002_async_retain_persists_operation_row()
    {
        var services = new ServiceCollection()
            .AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"))
            .BuildServiceProvider();
        var queue = new MemoryRetainQueue(services.GetRequiredService<IServiceScopeFactory>());
        using var db = Open();
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var bank = await banks.ResolveAsync(null, default);

        var op = await queue.EnqueueAsync(bank, MemoryBank.ServerWideKey,
            new MemoryRetainRequest("conteúdo async", Strategy: "verbatim"), default);
        Assert.Equal("queued", op.Status);
        Assert.Equal("retain", op.Kind);

        using var verify = Open();
        var row = await verify.MemoryOperations.SingleAsync(o => o.Id == op.Id);
        Assert.Equal(bank.Id, row.BankId);
        Assert.Contains("verbatim", row.PayloadJson);
        // Cooperative cancel on a queued op is signalled to the worker.
        Assert.True(queue.TryCancel(op.Id));
    }

    [Fact]
    public void RF005_chunker_splits_paragraphs_and_respects_budget()
    {
        var chunks = MemoryRetainService.Chunk("um\n\ndois\n\ntrês");
        Assert.Equal(3, chunks.Count);
        var big = new string('x', 2500) + "\n\n" + new string('y', 100);
        var split = MemoryRetainService.Chunk(big);
        // paragraph-per-chunk; >2000 splits at the boundary → 2+1 = 3 chunks.
        Assert.Equal(3, split.Count);
    }

    [Fact]
    public void RF001_parse_facts_extracts_types_dates_entities()
    {
        var raw = """
            [{"text":"A migração para Postgres foi decidida","fact_type":"world",
              "event_date":"2026-03-15","entities":[{"name":"Postgres","type":"database"}]},
             {"text":"Eu subi o release 2.4","fact_type":"experience"}]
            """;
        var facts = MemoryRetainService.ParseFacts(raw);
        Assert.Equal(2, facts.Count);
        Assert.Equal("world", facts[0].FactType);
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), facts[0].EventDate);
        Assert.Single(facts[0].Entities);
        Assert.Equal("experience", facts[1].FactType);
    }

    [Fact]
    public void RF001_parse_facts_tolerates_fences_and_garbage()
    {
        Assert.Empty(MemoryRetainService.ParseFacts(null));
        Assert.Empty(MemoryRetainService.ParseFacts("not json"));
        Assert.Single(MemoryRetainService.ParseFacts(
            "```json\n[{\"text\":\"x\",\"fact_type\":\"world\"}]\n```"));
    }
}
