using KnowledgeHub.Server.Assistant;
using KnowledgeHub.Server.Context;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Context;

/// <summary>SPEC-20261004-context-snapshots RF-006 — compaction core.</summary>
public class ContextCompactionServiceTests
{
    private sealed class NullAssistantProvider : IAssistantChatClientProvider
    {
        public IChatClient? ForSubtask(string subtask, IChatClient? main) => null;
        public void Invalidate() { }
    }

    private sealed class StubAssistantProvider : IAssistantChatClientProvider
    {
        private readonly IChatClient _client;
        public StubAssistantProvider(IChatClient client) => _client = client;
        public IChatClient? ForSubtask(string subtask, IChatClient? main) =>
            subtask == "summarize" ? _client : main;
        public void Invalidate() { }
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatResponse> _respond;
        public StubChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> respond) => _respond = respond;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(_respond(messages));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ContextCompactionService Svc(
        IAssistantChatClientProvider? provider = null, ContextOptions? options = null) =>
        new(provider ?? new NullAssistantProvider(),
            Options.Create(options ?? new ContextOptions()),
            NullLogger<ContextCompactionService>.Instance);

    private static ContextItem Item(string kind, int contentChars, double? score = null, string? tool = null) =>
        new() { Kind = kind, Content = new string('x', contentChars), Score = score, Tool = tool };

    [Fact]
    public async Task StrategyNone_TrimsBudget_ProtectedKindsSurvive()
    {
        // Orçamento ~300 tokens; passage (lixo) cai antes de decision.
        var items = new List<ContextItem>
        {
            Item("decision", 400),
            Item("passage", 800, score: 0.1),
            Item("tool_output", 800, tool: "t")
        };
        var result = await Svc().CompactAsync(items, "none", 300, default);
        Assert.Single(result.Items);
        Assert.Equal("decision", result.Items[0].Kind);
        Assert.Equal(0, result.Stats.ElidedCount);
        Assert.Equal(2, result.Stats.DroppedCount);
        Assert.True(result.WithinBudget);
    }

    [Fact]
    public async Task Elision_OnlyOldDisposableItems_KeepTailIntact()
    {
        var opts = new ContextOptions { KeepMinLastItems = 2, ElisionTokenLimit = 50 };
        var items = new List<ContextItem>
        {
            Item("tool_output", 1000, tool: "search_knowledge"), // elidível
            Item("decision", 1000),                             // protegido
            Item("passage", 1000),                              // keep-tail
            Item("tool_output", 1000)                           // keep-tail
        };
        var result = await Svc(options: opts).CompactAsync(items, "elision", 100_000, default);
        Assert.Equal(4, result.Items.Count);
        Assert.Equal(1, result.Stats.ElidedCount);
        Assert.StartsWith("[Output elided:", result.Items[0].Content);
        Assert.Contains("Tool: search_knowledge", result.Items[0].Content);
        Assert.Equal(new string('x', 1000), result.Items[1].Content);
        Assert.Equal(new string('x', 1000), result.Items[3].Content);
    }

    [Fact]
    public async Task Elision_IsIdempotent()
    {
        var opts = new ContextOptions { KeepMinLastItems = 1, ElisionTokenLimit = 50 };
        var items = new List<ContextItem> { Item("tool_output", 2000), Item("note", 10) };
        var svc = Svc(options: opts);
        var once = await svc.CompactAsync(items, "elision", 100_000, default);
        var twice = await svc.CompactAsync(once.Items, "elision", 100_000, default);
        Assert.Equal(once.Items[0].Content, twice.Items[0].Content);
        Assert.Equal(0, twice.Stats.ElidedCount);
    }

    [Fact]
    public async Task Summary_NoLlm_DeterministicFallback()
    {
        // Fallback determinístico: summary substitui elegíveis, summaryFallback=true.
        var items = new List<ContextItem>
        {
            Item("decision", 200),
            Item("tool_output", 4000, tool: "t1"),
            Item("tool_output", 4000, tool: "t2")
        };
        var opts = new ContextOptions { KeepMinLastItems = 0, ElisionTokenLimit = 10 };
        var result = await Svc(options: opts)
            .CompactAsync(items, "elision+summary", 60, default);
        Assert.True(result.Stats.SummaryFallback);
        Assert.Contains(result.Items, i => i.Kind == "summary");
        Assert.Contains("summarized content",
            result.Items.Single(i => i.Kind == "summary").Content);
        // decision nunca é removido nem fundido
        Assert.Contains(result.Items, i => i.Kind == "decision" && i.Content == new string('x', 200));
    }

    [Fact]
    public async Task Summary_LlmSuccess_UsesLlmOutput()
    {
        var client = new StubChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "LLM summary")));
        var provider = new StubAssistantProvider(client);
        var items = new List<ContextItem>
        {
            Item("tool_output", 4000, tool: "t"),
            Item("tool_output", 4000, tool: "t")
        };
        var opts = new ContextOptions { KeepMinLastItems = 0, ElisionTokenLimit = 10 };
        var result = await Svc(provider, opts)
            .CompactAsync(items, "elision+summary", 25, default);
        Assert.True(result.Stats.SummaryUsed);
        Assert.False(result.Stats.SummaryFallback);
        Assert.Contains(result.Items, i => i.Content == "LLM summary");
    }

    [Fact]
    public async Task Summary_LlmThrows_FallsBack()
    {
        var client = new StubChatClient(_ => throw new InvalidOperationException("boom"));
        var provider = new StubAssistantProvider(client);
        var items = new List<ContextItem>
        {
            Item("tool_output", 4000, tool: "t"),
            Item("tool_output", 4000, tool: "t")
        };
        var opts = new ContextOptions { KeepMinLastItems = 0, ElisionTokenLimit = 10 };
        var result = await Svc(provider, opts)
            .CompactAsync(items, "elision+summary", 25, default);
        Assert.False(result.Stats.SummaryUsed);
        Assert.True(result.Stats.SummaryFallback);
    }

    [Fact]
    public async Task DropOrder_PassageLowestScoreFirst_ThenOldestToolOutput()
    {
        // Total ~325; budget 250 → uma única remoção: a passage de menor score.
        var items = new List<ContextItem>
        {
            Item("passage", 400, score: 0.9),
            Item("passage", 400, score: 0.1),  // sai primeiro
            Item("tool_output", 400),          // sobrevive enquanto houver passage
            Item("decision", 100)
        };
        var result = await Svc().CompactAsync(items, "none", 250, default);
        Assert.Equal(new[] { "passage", "tool_output", "decision" },
            result.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(0.9, result.Items[0].Score);
        Assert.Equal(1, result.Stats.DroppedCount);

        // Budget apertado: caem as duas passages e o tool_output — decision fica.
        var tight = await Svc().CompactAsync(items, "none", 50, default);
        Assert.Single(tight.Items);
        Assert.Equal("decision", tight.Items[0].Kind);
        Assert.Equal(3, tight.Stats.DroppedCount);
    }

    [Fact]
    public async Task OnlyProtectedRemain_OverBudget_WithinBudgetFalse()
    {
        var items = new List<ContextItem> { Item("decision", 4000), Item("note", 4000) };
        var result = await Svc().CompactAsync(items, "none", 500, default);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(0, result.Stats.DroppedCount);
        Assert.False(result.WithinBudget);
    }

    [Fact]
    public async Task RelativeOrder_Preserved()
    {
        var items = new List<ContextItem>
        {
            Item("decision", 100),
            Item("passage", 400, score: 0.5),
            Item("note", 100),
            Item("tool_output", 400, tool: "t")
        };
        var result = await Svc().CompactAsync(items, "none", 100, default);
        // passage (score 0.5) cai, tool_output cai; restam decision, note na ordem
        Assert.Equal(new[] { "decision", "note" }, result.Items.Select(i => i.Kind).ToArray());
    }
}
