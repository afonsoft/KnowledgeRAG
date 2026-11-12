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
/// SPEC-20261112-memory-prompt-provenance RF-001..RF-005: provenance round-trip,
/// prompt-level dedupe, origin validation, defense screening, session filters,
/// truncation, and backward compatibility when provenance is absent.
/// </summary>
public class MemoryProvenanceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-prov-{Guid.NewGuid():N}.db");

    public MemoryProvenanceTests()
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

    /// <summary>Fake screen that redacts a marker — proves prompt text goes
    /// through the same defense pipeline as content (AC-004).</summary>
    private sealed class RedactingScreen : IMemoryDefenseScreen
    {
        public Task<string> ScreenAsync(string text, MemoryBank bank, CancellationToken ct) =>
            Task.FromResult(text.Replace("SECRET-TOKEN", "***REDACTED***"));
    }

    private MemoryRetainService Retain(KnowledgeHubDbContext db, IMemoryDefenseScreen? screen = null)
    {
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        return new MemoryRetainService(db, new MemoryRetainDeps(banks,
            new MemoryConfigResolver(new ConfigurationBuilder().Build()),
            new MemoryEntityLinker(db), screen ?? new NoOpMemoryDefenseScreen(),
            new ServiceCollection().BuildServiceProvider(),
            new FakeEmbeddings(), new MemoryConsolidationTrigger()),
            NullLogger<MemoryRetainService>.Instance);
    }

    private static MemoryUnitService Units(KnowledgeHubDbContext db) =>
        new(db, new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted)));

    [Fact]
    public async Task AC001_provenance_round_trips_on_document_and_units()
    {
        using var db = Open();
        var result = await Retain(db).RetainAsync(null, new MemoryRetainRequest(
            "O deploy falhou no nó 3.", Strategy: "verbatim",
            SourcePrompt: "por que o deploy quebrou?",
            SessionId: "thread-42", Origin: "passive"), default);

        Assert.False(result.PromptSeen);
        Assert.NotNull(result.DocumentId);
        var doc = await db.MemoryDocuments.SingleAsync();
        Assert.Equal("por que o deploy quebrou?", doc.SourcePrompt);
        Assert.Equal("thread-42", doc.SourceSessionId);
        Assert.Equal("passive", doc.Origin);
        Assert.Equal(64, doc.PromptHash!.Length);
        var unit = await db.MemoryUnits.Include(u => u.Document).SingleAsync();
        Assert.Equal(doc.Id, unit.Document!.Id);
    }

    [Fact]
    public async Task AC002_same_prompt_session_dedupes_before_extraction()
    {
        using var db = Open();
        var svc = Retain(db);
        var first = await svc.RetainAsync(null, new MemoryRetainRequest(
            "fato A", Strategy: "verbatim",
            SourcePrompt: "mesmo prompt", SessionId: "s1"), default);
        var second = await svc.RetainAsync(null, new MemoryRetainRequest(
            "fato B (conteúdo diferente, mesmo prompt)", Strategy: "verbatim",
            SourcePrompt: "mesmo prompt", SessionId: "s1"), default);

        Assert.True(second.PromptSeen);
        Assert.Equal(0, second.UnitsCreated);
        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Equal(1, await db.MemoryDocuments.CountAsync());
    }

    [Fact]
    public async Task AC003_no_provenance_preserves_old_behavior()
    {
        using var db = Open();
        var result = await Retain(db).RetainAsync(null,
            new MemoryRetainRequest("fato sem proveniência", Strategy: "verbatim"), default);

        Assert.False(result.PromptSeen);
        Assert.Null(result.DocumentId);
        Assert.Equal(0, await db.MemoryDocuments.CountAsync());
    }

    [Fact]
    public async Task RF003_invalid_origin_rejected_before_persistence()
    {
        using var db = Open();
        await Assert.ThrowsAsync<MemoryBadRequestException>(() =>
            Retain(db).RetainAsync(null, new MemoryRetainRequest(
                "fato", Strategy: "verbatim", Origin: "bogus"), default));
        Assert.Equal(0, await db.MemoryDocuments.CountAsync());
    }

    [Fact]
    public async Task AC004_source_prompt_passes_through_defense_screen()
    {
        using var db = Open();
        await Retain(db, new RedactingScreen()).RetainAsync(null, new MemoryRetainRequest(
            "fato", Strategy: "verbatim",
            SourcePrompt: "meu token é SECRET-TOKEN ok?", SessionId: "s1"), default);

        var doc = await db.MemoryDocuments.SingleAsync();
        Assert.DoesNotContain("SECRET-TOKEN", doc.SourcePrompt);
        Assert.Contains("***REDACTED***", doc.SourcePrompt);
    }

    [Fact]
    public async Task AC005_session_filter_on_list_and_dto_provenance()
    {
        using var db = Open();
        var svc = Retain(db);
        await svc.RetainAsync(null, new MemoryRetainRequest(
            "fato sessão um", Strategy: "verbatim",
            SourcePrompt: "p1", SessionId: "sess-1"), default);
        await svc.RetainAsync(null, new MemoryRetainRequest(
            "fato sessão dois", Strategy: "verbatim",
            SourcePrompt: "p2", SessionId: "sess-2"), default);

        var all = await Units(db).ListAsync(null, null, null, 20, 0, null, default);
        Assert.Equal(2, all.Count);
        var s1 = await Units(db).ListAsync(null, null, null, 20, 0, "sess-1", default);
        var dto = Assert.Single(s1);
        Assert.Equal("fato sessão um", dto.Text);
        Assert.Equal("p1", dto.SourcePrompt);
        Assert.Equal("sess-1", dto.SessionId);
        Assert.Equal("explicit", dto.Origin);
    }

    [Fact]
    public async Task AC006_recall_session_id_scopes_results()
    {
        using var db = Open();
        var svc = Retain(db);
        await svc.RetainAsync(null, new MemoryRetainRequest(
            "postgres migração março", Strategy: "verbatim",
            SourcePrompt: "p", SessionId: "sess-1"), default);
        await svc.RetainAsync(null, new MemoryRetainRequest(
            "postgres backup diário", Strategy: "verbatim",
            SourcePrompt: "q", SessionId: "sess-2"), default);

        var recall = new MemoryRecallService(db,
            new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted)),
            new FakeEmbeddings(),
            NullLogger<MemoryRecallService>.Instance);
        var only = await recall.RecallAsync(null,
            new MemoryRecallRequest("postgres", SessionId: "sess-1"), default);
        Assert.Single(only.Results);
        Assert.Equal("sess-1", only.Results[0].SessionId);
        Assert.Equal("p", only.Results[0].SourcePrompt);
        Assert.Equal("explicit", only.Results[0].Origin);
    }

    [Fact]
    public async Task AC007_five_kb_prompt_truncates_to_cap_with_metadata()
    {
        using var db = Open();
        var longPrompt = new string('x', 5_000);
        await Retain(db).RetainAsync(null, new MemoryRetainRequest(
            "fato", Strategy: "verbatim",
            SourcePrompt: longPrompt, SessionId: "s1"), default);

        var doc = await db.MemoryDocuments.SingleAsync();
        Assert.Equal(2_000, doc.SourcePrompt!.Length);
        Assert.Contains("promptTruncated", doc.MetadataJson);
        // AC-007: dedupe key must use the stored (truncated) prompt hash.
        var again = await Retain(db).RetainAsync(null, new MemoryRetainRequest(
            "outro fato", Strategy: "verbatim",
            SourcePrompt: longPrompt, SessionId: "s1"), default);
        Assert.True(again.PromptSeen);
    }

    [Fact]
    public async Task AC008_different_session_same_prompt_does_not_dedupe()
    {
        using var db = Open();
        var svc = Retain(db);
        await svc.RetainAsync(null, new MemoryRetainRequest(
            "fato A", Strategy: "verbatim",
            SourcePrompt: "prompt", SessionId: "s1"), default);
        var other = await svc.RetainAsync(null, new MemoryRetainRequest(
            "fato B", Strategy: "verbatim",
            SourcePrompt: "prompt", SessionId: "s2"), default);

        Assert.False(other.PromptSeen);
        Assert.Equal(1, other.UnitsCreated);
        Assert.Equal(2, await db.MemoryDocuments.CountAsync());
    }
}
