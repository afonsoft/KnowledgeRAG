namespace KnowledgeHub.Tests.Bench;

/// <summary>SPEC-20261113-agent-journey-bench RF-001: one executed MCP call —
/// tool name, wall-time, payload sizes and the error flag.</summary>
public sealed record StepMetric(
    string Tool,
    long Ms,
    int BytesIn,
    int BytesOut,
    bool IsError);

/// <summary>Outcome of one MCP tools/call — the structuredContent payload
/// (empty object when absent) plus the isError flag, for step assertions
/// and for later steps resolving dynamic arguments (e.g. operationId).</summary>
public sealed class StepOutcome
{
    public required string Tool { get; init; }
    public required System.Text.Json.JsonElement Structured { get; init; }
    public bool IsError { get; init; }
}

/// <summary>A journey step: tool + argument factory (sees prior outcomes,
/// so step N can reference ids returned by step N-1) + optional assertion
/// returning an error string (null = pass). PollMax > 1 turns the step into
/// a poll loop: repeat until the assertion passes or the budget runs out —
/// each attempt counts as one call.</summary>
public sealed record JourneyStep(
    string Tool,
    Func<IReadOnlyList<StepOutcome>, object> Args,
    Func<StepOutcome, string?>? Assert = null,
    int PollMax = 1,
    int PollDelayMs = 0)
{
    public static JourneyStep Call(string tool, object args, Func<StepOutcome, string?>? assert = null)
        => new(tool, _ => args, assert);
}

/// <summary>A named journey: deterministic ordered steps against a fresh bank.</summary>
public sealed record Journey(
    string Name,
    IReadOnlyList<JourneyStep> Steps);

/// <summary>Result of one journey run: per-step metrics + aggregates.</summary>
public sealed class JourneyResult
{
    public required string Name { get; init; }
    public List<StepMetric> Steps { get; } = [];
    public List<StepOutcome> Outcomes { get; } = [];
    public int Calls => Steps.Count;
    public long TotalMs => Steps.Sum(s => s.Ms);
    public long BytesIn => Steps.Sum(s => s.BytesIn);
    public long BytesOut => Steps.Sum(s => s.BytesOut);
}

/// <summary>Baseline entry per journey: hard gate on calls + wide cap on ms.</summary>
public sealed record JourneyBaseline(int MaxCalls, int BudgetMs, int WarnMs);
