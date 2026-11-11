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
/// SPEC-20261111-memory-reflect-directives: agentic read-only loop,
/// citation validation vs retrieved set, directive scoping, dispositions,
/// structured output second pass, budgets.
/// </summary>
public class MemoryReflectTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-ref-{Guid.NewGuid():N}.db");

    public MemoryReflectTests()
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

    /// <summary>Scripted chat whose messages may include function calls.</summary>
    private sealed class LoopChat : IChatClient
    {
        private readonly Queue<ChatMessage> _replies;
        public LoopChat(params ChatMessage[] replies) => _replies = new(replies);
        public List<ChatMessage> Received { get; } = [];
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Received.AddRange(messages);
            var reply = _replies.Count > 0 ? _replies.Dequeue()
                : new ChatMessage(ChatRole.Assistant, "no more scripted replies");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatMessage FnCall(string name, object args) =>
        new(ChatRole.Assistant,
        [
            new FunctionCallContent($"c{Guid.NewGuid():N}"[..12], name,
                new Dictionary<string, object?>(args.GetType()
                    .GetProperties()
                    .ToDictionary(p => p.Name, p => p.GetValue(args))))
        ]);

    private MemoryReflectService Service(KnowledgeHubDbContext db, IChatClient? chat,
        Dictionary<string, string?>? cfg = null)
    {
        var sc = new ServiceCollection();
        if (chat is not null) sc.AddSingleton(chat);
        var sp = sc.BuildServiceProvider();
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        return new MemoryReflectService(db, banks,
            new MemoryRecallService(db, banks, new FakeEmbeddings(),
                NullLogger<MemoryRecallService>.Instance),
            new MemoryConfigResolver(
                new ConfigurationBuilder().AddInMemoryCollection(cfg ?? []).Build()),
            sp);
    }

    private async Task<(MemoryBank Bank, MemoryUnit Fact, MemoryUnit Obs)> SeedAsync(
        KnowledgeHubDbContext db, string? mission = null, string? disposition = null)
    {
        var bank = new MemoryBank
        {
            BankKey = "default",
            ApiKeyId = MemoryBank.ServerWideKey,
            Name = "d",
            Mission = mission,
            DispositionJson = disposition
        };
        db.MemoryBanks.Add(bank);
        var emb = new FakeEmbeddings();
        var fact = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "O deploy falhou por falta de memória",
            FactType = "world",
            ContentHash = "h1",
            Embedding = MemoryRetainService.ToBytes(await emb.EmbedAsync("deploy falhou memória")),
            EmbeddingModel = emb.ModelId
        };
        var obs = new MemoryUnit
        {
            BankId = bank.Id,
            Text = "Deploys falham por memória insuficiente",
            FactType = "observation",
            ContentHash = "h2",
            SourceMemoryIdsJson = JsonSerializer.Serialize(new[] { fact.Id }),
            ProofCount = 1,
            Embedding = MemoryRetainService.ToBytes(await emb.EmbedAsync("deploy memória insuficiente")),
            EmbeddingModel = emb.ModelId
        };
        db.MemoryUnits.AddRange(fact, obs);
        await db.SaveChangesAsync();
        return (bank, fact, obs);
    }

    [Fact]
    public async Task RF002_agentic_loop_searches_then_done_with_citations()
    {
        using var db = Open();
        var (bank, fact, obs) = await SeedAsync(db);
        var chat = new LoopChat(
            FnCall("search_observations", new { query = "deploy" }),
            FnCall("done", new { answer = "Deploys falham por memória", based_on = new[] { obs.Id.ToString() } }));
        var result = await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("por que o deploy falha?", IncludeTrace: true), default);

        Assert.Equal("Deploys falham por memória", result.Text);
        Assert.Single(result.BasedOn!);
        Assert.Equal(obs.Id, result.BasedOn![0].Id);
        Assert.Equal("observation", result.BasedOn![0].Type);
        Assert.NotNull(result.Trace);
        Assert.Equal(2, result.Trace!.Calls.Count);
        Assert.Equal("search_observations", result.Trace.Calls[0].Tool);
        Assert.Empty(result.Trace.DroppedCitations);
        Assert.False(result.Trace.Uncited);
    }

    [Fact]
    public async Task RF003_fabricated_citations_are_dropped_and_traced()
    {
        using var db = Open();
        var (_, fact, obs) = await SeedAsync(db);
        var bogus = Guid.NewGuid();
        var chat = new LoopChat(
            FnCall("done", new
            {
                answer = "resposta",
                based_on = new[] { obs.Id.ToString(), bogus.ToString() }
            }));
        var result = await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("q", IncludeTrace: true), default);

        // The observation was never retrieved in this run (no search call) —
        // both ids are outside the retrieved set and get dropped.
        Assert.Empty(result.BasedOn!);
        Assert.Equal(2, result.Trace!.DroppedCitations.Count);
        Assert.True(result.Trace.Uncited);
    }

    [Fact]
    public async Task RF003_citation_valid_only_when_retrieved_this_call()
    {
        using var db = Open();
        var (_, fact, obs) = await SeedAsync(db);
        var chat = new LoopChat(
            FnCall("recall_facts", new { query = "deploy" }),
            FnCall("done", new { answer = "a", based_on = new[] { fact.Id.ToString(), obs.Id.ToString() } }));
        var result = await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("q", IncludeTrace: true), default);

        // recall_facts fetched fact+obs? — factType filter is world,experience
        // so obs is NOT in the retrieved set → dropped.
        Assert.Single(result.BasedOn!);
        Assert.Equal(fact.Id, result.BasedOn![0].Id);
        Assert.Single(result.Trace!.DroppedCitations);
    }

    [Fact]
    public async Task RF002_directives_and_disposition_enter_system_prompt()
    {
        using var db = Open();
        var (bank, _, _) = await SeedAsync(db, mission: "memória do squad X",
            disposition: """{"skepticism":5,"literalism":2,"empathy":1}""");
        db.MemoryDirectives.AddRange(
            new MemoryDirective { BankId = bank.Id, Content = "sempre pt-BR", Priority = 10 },
            new MemoryDirective { BankId = bank.Id, Content = "tagged only", Tags = ["ops"] },
            new MemoryDirective { BankId = bank.Id, Content = "inativa", IsActive = false });
        await db.SaveChangesAsync();

        var chat = new LoopChat(FnCall("done", new { answer = "ok", based_on = Array.Empty<string>() }));
        await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("q"), default);

        var system = chat.Received.First(m => m.Role == ChatRole.System).Text;
        Assert.Contains("memória do squad X", system);
        Assert.Contains("skeptical", system);
        Assert.Contains("sempre pt-BR", system);        // global directive
        Assert.DoesNotContain("tagged only", system); // tag-scoped, no match
        Assert.DoesNotContain("inativa", system);
    }

    [Fact]
    public async Task RF002_tagged_directives_apply_on_match()
    {
        using var db = Open();
        var (bank, _, _) = await SeedAsync(db);
        db.MemoryDirectives.Add(new MemoryDirective
        { BankId = bank.Id, Content = "regra de ops", Tags = ["ops"] });
        await db.SaveChangesAsync();
        var chat = new LoopChat(FnCall("done", new { answer = "ok", based_on = Array.Empty<string>() }));
        await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("q", Tags: ["ops"]), default);
        Assert.Contains("regra de ops",
            chat.Received.First(m => m.Role == ChatRole.System).Text);
    }

    [Fact]
    public async Task RF004_structured_output_second_pass()
    {
        using var db = Open();
        await SeedAsync(db);
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}}}""").RootElement;
        var chat = new LoopChat(
            FnCall("done", new { answer = "texto livre", based_on = Array.Empty<string>() }),
            new ChatMessage(ChatRole.Assistant, """{"summary":"resumo estruturado"}"""));
        var result = await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("q", ResponseSchema: schema), default);

        Assert.NotNull(result.StructuredOutput);
        Assert.Equal("resumo estruturado",
            result.StructuredOutput!.Value.GetProperty("summary").GetString());
        Assert.Null(result.StructuredOutputError);
        Assert.Equal(2, chat.Calls); // reflect loop + extraction pass
    }

    [Fact]
    public async Task RF004_bad_structured_output_returns_error_not_exception()
    {
        using var db = Open();
        await SeedAsync(db);
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var chat = new LoopChat(
            FnCall("done", new { answer = "a", based_on = Array.Empty<string>() }),
            new ChatMessage(ChatRole.Assistant, "not json"),
            new ChatMessage(ChatRole.Assistant, "still not json"));
        var result = await Service(db, chat).ReflectAsync(null,
            new MemoryReflectRequest("q", ResponseSchema: schema), default);
        Assert.Null(result.StructuredOutput);
        Assert.NotNull(result.StructuredOutputError);
    }

    [Fact]
    public async Task RF002_natural_finish_extracts_based_on_block()
    {
        using var db = Open();
        var (_, fact, _) = await SeedAsync(db);
        var chat = new LoopChat(
            FnCall("recall_facts", new { query = "deploy" }),
            new ChatMessage(ChatRole.Assistant,
                $$"""A resposta é X. {"answer":"a resposta final","based_on":["{{fact.Id}}"]}"""));
        var result = await Service(db, chat).ReflectAsync(null, new MemoryReflectRequest("q"), default);
        Assert.Equal("a resposta final", result.Text);
        Assert.Single(result.BasedOn!);
        Assert.Equal(fact.Id, result.BasedOn![0].Id);
    }

    [Fact]
    public async Task RF001_directives_crud()
    {
        using var db = Open();
        var (bank, _, _) = await SeedAsync(db);
        var banks = new MemoryBankService(db, new FakeScope(CallerScope.Unrestricted));
        var svc = new MemoryDirectiveService(db, banks);

        var d = await svc.CreateAsync(null, "idioma", "sempre pt-BR", 10, null, ["geral"], default);
        Assert.Equal(10, d.Priority);
        var list = await svc.ListAsync(null, false, default);
        Assert.Single(list);
        var updated = await svc.UpdateAsync(null, d.Id, isActive: false, priority: 5, null, null, default);
        Assert.False(updated.IsActive);
        Assert.Empty(await svc.ListAsync(null, false, default));
        Assert.Single(await svc.ListAsync(null, true, default));
        Assert.True(await svc.DeleteAsync(null, d.Id, default));
        await Assert.ThrowsAsync<MemoryNotFoundException>(() =>
            svc.DeleteAsync(null, d.Id, default));
    }

    [Fact]
    public async Task RF002_reflect_requires_chat_and_existing_bank()
    {
        using var db = Open();
        await Assert.ThrowsAsync<MemoryServiceUnavailableException>(() =>
            Service(db, chat: null).ReflectAsync(null, new MemoryReflectRequest("q"), default));
        var chat = new LoopChat(FnCall("done", new { answer = "a", based_on = Array.Empty<string>() }));
        await Assert.ThrowsAsync<MemoryNotFoundException>(() =>
            Service(db, chat).ReflectAsync("nope", new MemoryReflectRequest("q"), default));
    }
}
