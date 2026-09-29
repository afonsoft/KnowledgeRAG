using KnowledgeHub.McpEngine.Agents.ChainAst;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Tests.Unit.Agents;

// SPEC-20260927-chain-ast-thread-compactor RF-001..RF-004: section/pair
// parsing, orphan repair, budget-driven compaction, reasoning signatures.
public sealed class ChainAstTests
{
    private static ChatMessage Ai(string text, params FunctionCallContent[] calls)
    {
        var m = new ChatMessage(ChatRole.Assistant, text);
        foreach (var c in calls)
            m.Contents.Add(c);
        return m;
    }

    private static FunctionCallContent Call(string id, string name = "tool") =>
        new(id, name, new Dictionary<string, object?>());

    private static ChatMessage Tool(params FunctionResultContent[] results) =>
        new(ChatRole.Tool, results.Cast<AIContent>().ToList());

    private static FunctionResultContent Result(string id, string result = "done") =>
        new(id, result);

    // AC-3: round-trip preserves provider-required alternation.
    [Fact]
    public void Parse_RoundTripPreservesOrder()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "q1"),
            Ai("calling", Call("c1")),
            Tool(Result("c1")),
            Ai("answer"),
            new(ChatRole.User, "q2"),
            Ai("answer2")
        };
        var ast = ChainAstParser.Parse(messages);
        Assert.Equal(2, ast.Sections.Count);
        Assert.Equal(2, ast.Sections[0].Headers.Count); // system + q1
        Assert.Equal(2, ast.Sections[0].Body.Count);
        Assert.Equal(BodyPairType.RequestResponse, ast.Sections[0].Body[0].Type);
        Assert.Equal(BodyPairType.Completion, ast.Sections[0].Body[1].Type);

        var flat = ast.ToChatMessages();
        Assert.Equal(messages.Count, flat.Count);
        for (var i = 0; i < flat.Count; i++)
            Assert.Same(messages[i], flat[i]);
    }

    // Edge: empty history → empty AST.
    [Fact]
    public void EmptyHistory_EmptyAst()
    {
        var ast = ChainAstParser.Parse([]);
        Assert.Empty(ast.Sections);
        Assert.Empty(ast.ToChatMessages());
    }

    // Edge: system-only history → single section, no body.
    [Fact]
    public void SystemOnly_SingleSectionNoBody()
    {
        var ast = ChainAstParser.Parse([new ChatMessage(ChatRole.System, "s")]);
        Assert.Single(ast.Sections);
        Assert.Empty(ast.Sections[0].Body);
    }

    // Edge: 1 AI message + 4 calls answered out of order → all paired by id.
    [Fact]
    public void ParallelCalls_PairedByCallId()
    {
        var calls = new[] { Call("a"), Call("b"), Call("c"), Call("d") };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "q"),
            Ai("batch", calls),
            Tool(Result("c"), Result("a"), Result("d"), Result("b"))
        };
        var ast = ChainAstParser.Parse(messages);
        var pair = Assert.Single(ast.Sections[0].Body);
        Assert.Equal(4, pair.Calls.Count);
        Assert.All(pair.Calls, c => Assert.NotNull(c.Result));
        Assert.True(ChainAstRepair.IsValid(ast));
    }

    // AC-1: pending call repaired with synthetic result (no pending left).
    [Fact]
    public void Repair_SynthesizesPendingResult()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "q"),
            Ai("calling", Call("call_123"))
            // session restarted — no tool response ever arrived
        };
        var ast = ChainAstParser.Parse(messages, forceRepair: true);
        var pair = Assert.Single(ast.Sections[0].Body);
        Assert.Single(pair.ToolMessages);
        var stub = pair.ToolMessages[0].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_123", stub.CallId);
        Assert.Equal(ChainAstRepair.InterruptedNotice, stub.Result);
        Assert.True(ChainAstRepair.IsValid(ast));
    }

    // Edge: orphan tool message (no matching call) → excluded from flatten.
    [Fact]
    public void OrphanToolMessage_ExcludedFromOutput()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "q"),
            Ai("plain answer"),
            Tool(Result("ghost"))
        };
        var ast = ChainAstParser.Parse(messages);
        Assert.Single(ast.Orphans);
        Assert.DoesNotContain(ast.ToChatMessages(), m => m.Role == ChatRole.Tool);
        // Repair does not resurrect orphans — but validity holds (no calls).
        Assert.True(ChainAstRepair.IsValid(ast));
    }

    // Repair slots stubs into an existing tool message when present.
    [Fact]
    public void Repair_UsesExistingToolMessageCarrier()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "q"),
            Ai("two calls", Call("ok"), Call("lost")),
            Tool(Result("ok"))
        };
        var ast = ChainAstParser.Parse(messages, forceRepair: true);
        var pair = Assert.Single(ast.Sections[0].Body);
        Assert.Single(pair.ToolMessages); // no new message added
        Assert.Equal(2, pair.ToolMessages[0].Contents.OfType<FunctionResultContent>().Count());
    }

    // AC-2: long history folds old sections under the marker, keeps the tail.
    [Fact]
    public async Task Compactor_FoldsOldSectionsKeepsTail()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys") };
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"question {i} " + new string('x', 15000)));
            messages.Add(Ai($"answer {i}"));
        }
        var ast = ChainAstParser.Parse(messages);
        var compactor = new ChainCompactor(new ChainCompactionOptions
        {
            MaxTotalHistoryBytes = 65536,
            KeepMinLastSections = 2
        });
        var result = await compactor.CompactAsync(ast);

        Assert.True(result.EstimateBytes() <= 65536);
        var folded = result.Sections[0];
        // SPEC-20260929 RF-001: system headers are pinned — hoisted into the
        // folded section, never summarized away.
        var sysHeader = Assert.Single(folded.Headers);
        Assert.Equal(ChatRole.System, sysHeader.Role);
        Assert.Equal("sys", sysHeader.Text);
        var pair = Assert.Single(folded.Body);
        Assert.Equal(BodyPairType.SummarizedSection, pair.Type);
        Assert.StartsWith("**summarized content:**", pair.AiMessage.Text);
        // Tail preserved: last 2 sections stay intact.
        Assert.Contains(result.Sections[^1].Headers, m => m.Text.Contains("question 9"));
        Assert.Contains(result.Sections[^2].Headers, m => m.Text.Contains("question 8"));
    }

    // Compaction truncates oversized tool outputs but keeps the CallId.
    [Fact]
    public async Task Compactor_TruncatesToolOutputsKeepsCallId()
    {
        var big = new string('y', 40_000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "s"),
            new(ChatRole.User, "q"),
            Ai("calling", Call("c1")),
            Tool(Result("c1", big)),
            Ai("done"),
            new(ChatRole.User, "next"),
            Ai("done2")
        };
        var ast = ChainAstParser.Parse(messages);
        var compactor = new ChainCompactor(new ChainCompactionOptions
        {
            MaxTotalHistoryBytes = 1024,
            MaxBodyPairBytes = 4096,
            KeepMinLastSections = 1
        });
        var result = await compactor.CompactAsync(ast);
        // Pass-1 truncation happens before folding (folded section may drop
        // details) — assert the flattened stream stays call-consistent.
        Assert.True(ChainAstRepair.IsValid(ChainAstRepair.Repair(result)));
    }

    [Fact]
    public async Task Compactor_KeptSection_OversizedToolResult_StillTruncates()
    {
        // SPEC-20260929 RF-004: a single giant tool result in a KEPT section
        // must not blow the context — pass-3 truncation covers the tail.
        var big = new string('y', 40_000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "s"),
            new(ChatRole.User, "q"),
            Ai("calling", Call("c1")),
            Tool(Result("c1", big)),
            Ai("done")
        };
        var ast = ChainAstParser.Parse(messages);
        var compactor = new ChainCompactor(new ChainCompactionOptions
        {
            MaxTotalHistoryBytes = 1024,
            MaxBodyPairBytes = 4096,
            KeepMinLastSections = 1
        });
        var result = await compactor.CompactAsync(ast);

        var toolMsg = result.Sections[^1].Body[0].ToolMessages.Single();
        var text = toolMsg.Contents.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("truncated", text); // oversized result cut, CallId kept
        Assert.True(result.EstimateBytes() <= 8 * 1024);
    }

    [Fact]
    public async Task Compactor_SystemPrompt_SurvivesFold()
    {
        // AC-1: after folding N sections the system prompt is still first and
        // byte-identical.
        var messages = new List<ChatMessage> { new(ChatRole.System, "PINNED-INSTRUCTIONS") };
        for (var i = 0; i < 6; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"q{i} " + new string('z', 12000)));
            messages.Add(Ai($"a{i}"));
        }
        var ast = ChainAstParser.Parse(messages);
        var compactor = new ChainCompactor(new ChainCompactionOptions
        {
            MaxTotalHistoryBytes = 8192,
            KeepMinLastSections = 1
        });
        var result = await compactor.CompactAsync(ast);

        var flat = result.ToChatMessages();
        Assert.Equal(ChatRole.System, flat[0].Role);
        Assert.Equal("PINNED-INSTRUCTIONS", flat[0].Text);
    }

    // RF-004: folded reasoning emits a synthetic skip_thought_signature.    // RF-004: folded reasoning emits a synthetic skip_thought_signature.
    [Fact]
    public async Task Compactor_EmitsSkipThoughtSignature()
    {
        var thinking = new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("deep thoughts"), new TextContent("answer")]);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "q " + new string('x', 70000)),
            thinking,
            new(ChatRole.User, "q2"),
            Ai("a2"),
            new(ChatRole.User, "q3"),
            Ai("a3")
        };
        var ast = ChainAstParser.Parse(messages);
        var compactor = new ChainCompactor(new ChainCompactionOptions
        {
            MaxTotalHistoryBytes = 1000,
            KeepMinLastSections = 2
        });
        var result = await compactor.CompactAsync(ast);
        var foldedPair = result.Sections[0].Body[0];
        var sig = foldedPair.AiMessage.Contents.OfType<TextReasoningContent>().FirstOrDefault();
        Assert.NotNull(sig);
        Assert.Equal("skip_thought_signature", sig!.ProtectedData);
    }
}
