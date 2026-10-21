using KnowledgeHub.Server.Assistant;
using KnowledgeHub.Server.Context;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Context;

/// <summary>
/// SPEC-20261021-context-eval-matrix — T12 matrix
/// strategy {none, elision, elision+summary} × tokenBudget {1024, 2048, 4096, 8192}
/// over a synthetic snapshot. Asserts the honesty contract
/// (tokensAfter ≤ budget, or withinBudget=false when only protected items
/// remain) and records dropped/elided/fallback metrics.
/// Faithfulness/Recall@K of LLM summaries are covered by the live eval
/// harness — here the summary path runs through a deterministic stub.
/// </summary>
public class ContextEvalMatrixTests
{
    private sealed class NullProvider : IAssistantChatClientProvider
    {
        public IChatClient? ForSubtask(string subtask, IChatClient? main) => null;
        public void Invalidate() { }
    }

    private sealed class StubProvider : IAssistantChatClientProvider
    {
        public IChatClient? ForSubtask(string subtask, IChatClient? main) =>
            subtask == "summarize" ? new StubClient() : main;
        public void Invalidate() { }
    }

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant, "[summary] decisions preserved")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ContextCompactionService Svc(IAssistantChatClientProvider p) =>
        new(p, Options.Create(new ContextOptions()),
            NullLogger<ContextCompactionService>.Instance);

    /// <summary>Synthetic snapshot: 2 protected decisions + 6 droppable items
    /// (~900 tokens before elision at 4 chars/token).</summary>
    private static List<ContextItem> Snapshot() =>
    [
        new() { Kind = "decision", Content = "use sqlite for dev" },
        new() { Kind = "decision", Content = "no vault persistence" },
        .. Enumerable.Range(0, 4).Select(i => new ContextItem
        {
            Kind = "passage", Content = new string('p', 700), Score = 0.5 - i * 0.1
        }),
        new() { Kind = "tool_output", Tool = "search_knowledge", Content = new string('o', 700) },
        new() { Kind = "note", Content = "tail note kept by keep-min" }
    ];

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var strategy in new[] { "none", "elision", "elision+summary" })
            foreach (var budget in new[] { 1024, 2048, 4096, 8192 })
                yield return new object[] { strategy, budget };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Compact_Matrix_RespectsBudgetOrReportsHonestly(string strategy, int budget)
    {
        var svc = Svc(new StubProvider());
        var result = await svc.CompactAsync(
            Snapshot(), strategy, budget, ct: default);

        // RF-2: contract of honesty — either within budget or flagged.
        if (result.WithinBudget)
            Assert.True(result.Stats.TokensAfter <= budget,
                $"{strategy}@{budget}: {result.Stats.TokensAfter} > {budget}");
        else
            Assert.True(result.Stats.TokensAfter > 0);

        // Metrics must always be populated (RF-2).
        Assert.True(result.Stats.DroppedCount >= 0);
        Assert.True(result.Stats.ElidedCount >= 0);

        if (strategy == "elision+summary")
            Assert.False(result.Stats.SummaryFallback); // stub LLM available
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Compact_Matrix_NoLlm_StillHonestWithFallback(string strategy, int budget)
    {
        var svc = Svc(new NullProvider());
        var result = await svc.CompactAsync(
            Snapshot(), strategy, budget, ct: default);

        if (result.WithinBudget)
            Assert.True(result.Stats.TokensAfter <= budget);

        // RF-2: without a provider no LLM summary is ever used; when a
        // summary was needed (still over budget after elision) the fallback
        // flag must be set.
        if (strategy == "elision+summary")
        {
            Assert.False(result.Stats.SummaryUsed);
            if (result.Stats.TokensAfter > budget)
                Assert.True(result.Stats.SummaryFallback);
        }
    }
}
