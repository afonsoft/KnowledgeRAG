using System.Text.Json;

namespace KnowledgeHub.Tests.Bench.Journeys;

/// <summary>
/// SPEC-20261113-agent-journey-bench RF-002: the four deterministic journeys
/// covering the E29/E30 memory surface. Each journey runs against its own
/// bankKey so journeys never interfere on a shared host.
/// </summary>
public static class MemoryJourneys
{
    public static IReadOnlyList<Journey> All(string runId)
    {
        // Grounding models the SECOND session on the bank lifecycle already
        // populated — read tools resolve with create:false, so grounding on a
        // fresh bank would fail exactly like a real agent on a new project.
        var projectBank = $"bench-proj-{runId}";
        return
        [
            MemoryLifecycle(projectBank),
            SessionGrounding(projectBank),
            RetainDedupePrompt($"bench-dedupe-{runId}"),
            RetainAsyncPoll($"bench-async-{runId}"),
            SessionLifecycle(projectBank, $"sess-{runId}"),
            BankMergeFlow($"bench-src-{runId}", $"bench-dst-{runId}"),
            KnowledgeSearch(),
        ];
    }

    /// <summary>bank_create → retain(verbatim) → recall → context → timeline → stats → list.</summary>
    public static Journey MemoryLifecycle(string bank) => new("memory-lifecycle",
    [
        JourneyStep.Call("memory_bank_create", new { bankKey = bank, name = "bench lifecycle" }, Ok()),
        JourneyStep.Call("memory_retain", new
        {
            bankKey = bank,
            content = "Bench fact: the team picked sqlite-vec for local KNN in November 2026.",
            strategy = "verbatim",
            tags = new[] { "bench" },
        }, And(Ok(), IntAtLeast("unitsCreated", 1))),
        JourneyStep.Call("memory_recall", new { bankKey = bank, query = "vector search", topK = 3 },
            And(Ok(), HasProp("results"))),
        JourneyStep.Call("memory_context", new { bankKey = bank }, And(Ok(), HasProp("directives"))),
        JourneyStep.Call("memory_timeline", new { bankKey = bank, groupBy = "day" },
            And(Ok(), HasProp("buckets"))),
        JourneyStep.Call("memory_stats", new { bankKey = bank }, And(Ok(), IntAtLeast("totalUnits", 1))),
        JourneyStep.Call("memory_list", new { bankKey = bank, limit = 5 }, And(Ok(), IntAtLeast("count", 1))),
    ]);

    /// <summary>The E30 session-start pattern: context → recall → list. ≤3 calls.</summary>
    public static Journey SessionGrounding(string bank) => new("session-grounding",
    [
        JourneyStep.Call("memory_context", new { bankKey = bank }, And(Ok(), HasProp("directives"))),
        JourneyStep.Call("memory_recall", new { bankKey = bank, query = "recent decisions", topK = 5 },
            And(Ok(), HasProp("results"))),
        JourneyStep.Call("memory_list", new { bankKey = bank, limit = 5 }, And(Ok(), HasProp("units"))),
    ]);

    /// <summary>Same (bank, sessionId, promptHash) twice → 2nd call returns
    /// promptSeen:true, unitsCreated:0 — the provenance dedupe win.</summary>
    public static Journey RetainDedupePrompt(string bank) => new("retain-dedupe-prompt",
    [
        JourneyStep.Call("memory_retain", new
        {
            bankKey = bank,
            content = "First retain under the originating prompt.",
            strategy = "verbatim",
            sourcePrompt = "bench: remember the release checklist",
            sessionId = $"sess-{bank}",
        }, And(Ok(), IntAtLeast("unitsCreated", 1))),
        JourneyStep.Call("memory_retain", new
        {
            bankKey = bank,
            content = "Different content, same originating prompt — must not re-extract.",
            strategy = "verbatim",
            sourcePrompt = "bench: remember the release checklist",
            sessionId = $"sess-{bank}",
        }, And(Ok(), PropEquals("promptSeen", true), PropEquals("unitsCreated", 0))),
    ]);

    /// <summary>mode:async → operations_get poll until done (≤20 polls, 100ms).</summary>
    public static Journey RetainAsyncPoll(string bank) => new("retain-async-poll",
    [
        JourneyStep.Call("memory_retain", new
        {
            bankKey = bank,
            content = "Async bench fact: durable memory survives the session boundary.",
            strategy = "verbatim",
            mode = "async",
        }, And(Ok(), HasProp("operationId"))),
        new JourneyStep("operations_get",
            Args: outcomes => new
            {
                id = outcomes[^1].Structured.GetProperty("operationId").GetString(),
            },
            Assert: And(Ok(), PropEquals("status", "done")),
            PollMax: 20,
            PollDelayMs: 100),
    ]);

    /// <summary>SPEC-20261114-bench-v2: begin → context sees open session →
    /// end → session_list shows the closed row. The E32 lifecycle loop.</summary>
    public static Journey SessionLifecycle(string bank, string sessionId) => new("session-lifecycle",
    [
        JourneyStep.Call("memory_session_begin", new { bankKey = bank, sessionId, label = "bench" },
            And(Ok(), PropEquals("alreadyOpen", false))),
        JourneyStep.Call("memory_context", new { bankKey = bank },
            And(Ok(), HasProp("openSessions"))),
        JourneyStep.Call("memory_session_end", new { bankKey = bank, sessionId, summary = "bench done" },
            And(Ok(), PropEquals("ended", true))),
        JourneyStep.Call("memory_session_list", new { bankKey = bank },
            And(Ok(), IntAtLeast("count", 1))),
    ]);

    /// <summary>SPEC-20261114-bench-v2: merge source→target then recall through
    /// the OLD key — the tombstone redirect must keep it resolving.</summary>
    public static Journey BankMergeFlow(string source, string target) => new("bank-merge-flow",
    [
        JourneyStep.Call("memory_bank_create", new { bankKey = source, name = "merge source" }, Ok()),
        JourneyStep.Call("memory_retain", new
        {
            bankKey = source,
            content = "Bench fact written into the soon-to-be-merged bank.",
            strategy = "verbatim",
        }, And(Ok(), IntAtLeast("unitsCreated", 1))),
        JourneyStep.Call("memory_bank_create", new { bankKey = target, name = "merge target" }, Ok()),
        JourneyStep.Call("memory_bank_merge", new { sourceKey = source, targetKey = target },
            And(Ok(), PropEquals("merged", true))),
        JourneyStep.Call("memory_recall", new { bankKey = source, query = "merged bank", topK = 3 },
            And(Ok(), HasProp("results"))),
        JourneyStep.Call("memory_bank_list", new { includeMerged = true },
            And(Ok(), HasProp("banks"))),
    ]);

    /// <summary>SPEC-20261114-bench-v2: the RAG arm — two hybrid searches on the
    /// seeded vault source (empty result sets are fine; this measures friction).</summary>
    public static Journey KnowledgeSearch() => new("knowledge-search",
    [
        JourneyStep.Call("search_knowledge", new { query = "vector search memory bank" },
            And(Ok(), HasProp("results"))),
        JourneyStep.Call("search_knowledge", new { query = "session lifecycle context" },
            And(Ok(), HasProp("results"))),
    ]);

    // ---- assert helpers: each returns an error string or null (pass) ----

    private static Func<StepOutcome, string?> Ok() =>
        o => o.IsError ? "isError=true" : null;

    private static Func<StepOutcome, string?> HasProp(string name) => o =>
        o.IsError ? "isError=true"
        : o.Structured.TryGetProperty(name, out _) ? null
        : $"missing property '{name}' in {o.Structured.GetRawText()}";

    private static Func<StepOutcome, string?> IntAtLeast(string name, int min) => o =>
        o.IsError ? "isError=true"
        : o.Structured.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() >= min ? null
        : $"expected {name}>={min} in {o.Structured.GetRawText()}";

    private static Func<StepOutcome, string?> PropEquals(string name, object expected) => o =>
        o.IsError ? "isError=true"
        : !o.Structured.TryGetProperty(name, out var p) ? $"missing property '{name}'"
        : Matches(p, expected) ? null
        : $"expected {name}={expected}, got {p.GetRawText()}";

    private static bool Matches(JsonElement p, object expected) => expected switch
    {
        bool b => p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean() == b,
        int i => p.ValueKind == JsonValueKind.Number && p.GetInt32() == i,
        string s => p.ValueKind == JsonValueKind.String && p.GetString() == s,
        _ => false,
    };

    private static Func<StepOutcome, string?> And(params Func<StepOutcome, string?>[] asserts) => o =>
        asserts.Select(a => a(o)).FirstOrDefault(f => f is not null);
}
