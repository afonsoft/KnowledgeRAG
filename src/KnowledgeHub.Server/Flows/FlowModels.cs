using System.Text.Json.Nodes;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Flows;

/// <summary>Per-run execution state carried across steps.</summary>
public sealed class FlowExecContext
{
    /// <summary>Scoped services of the invoking request (DbContext, catalog,
    /// chat client, HttpContextAccessor — same surface ToolCallContext uses).</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Limits for this run (from Flow:* configuration).</summary>
    public required FlowLimits Limits { get; init; }

    /// <summary>Declared inputs after defaulting/validation.</summary>
    public JsonObject Vars { get; } = new();

    /// <summary>step id → step output.</summary>
    public Dictionary<string, JsonNode?> StepOutputs { get; } = new(StringComparer.Ordinal);

    /// <summary>step id → error message (failed/skipped steps).</summary>
    public Dictionary<string, string?> StepErrors { get; } = new(StringComparer.Ordinal);

    /// <summary>Total executed steps across nesting — MaxSteps cap.</summary>
    public int ExecutedCount;

    /// <summary>Total foreach iterations consumed — MaxIterations cap.</summary>
    public int IterationsCount;

    /// <summary>Run-wide flat trace — nested steps (condition branches,
    /// foreach bodies) append here so the audit trail is complete.</summary>
    public List<FlowStepResult> Trace { get; } = new();

    /// <summary>Optional SSE sink: step lifecycle events for ?stream=1.</summary>
    public Func<FlowStreamEvent, CancellationToken, ValueTask>? Sink { get; init; }

    /// <summary>ApiKey id of the caller (recorded on the run; null = cookie admin).</summary>
    public Guid? ApiKeyId { get; init; }

    /// <summary>Set by the <c>approval</c> step handler: the run must suspend
    /// here until a human resolves the gate. The engine turns it into a
    /// <see cref="FlowSuspendException"/> before the next step.</summary>
    public PendingApproval? PendingApproval { get; set; }

    /// <summary>Step ids carried in from a <see cref="FlowResumeState"/> — only
    /// these may be skipped (each is consumed on first match). Distinct from
    /// <see cref="StepOutputs"/> so steps executed during THIS run (e.g. a
    /// nested step inside a foreach's later iterations) are never skipped.</summary>
    public HashSet<string>? RestoredStepIds { get; set; }
}

/// <summary>A mid-flow HITL gate: the step that asked for approval plus the
/// operator-facing message shown in /api/approvals.</summary>
public sealed class PendingApproval
{
    public required string StepId { get; init; }
    public required string Message { get; init; }
}

/// <summary>Serialised execution state persisted on a suspended FlowRun —
/// enough for the engine to skip already-executed steps on resume.</summary>
public sealed class FlowResumeState
{
    public JsonObject? Vars { get; set; }
    public Dictionary<string, JsonNode?>? StepOutputs { get; set; }
    public Dictionary<string, string?>? StepErrors { get; set; }
    public int ExecutedCount { get; set; }
    public int IterationsCount { get; set; }
    public List<FlowStepResultDto>? Trace { get; set; }
    /// <summary>The approval step's id — its resolution is seeded into
    /// StepOutputs on resume.</summary>
    public string? PendingStepId { get; set; }
}

/// <summary>Engine abort that suspends instead of failing — carries the
/// pending gate up to FlowService, which persists the resume state.</summary>
public sealed class FlowSuspendException(PendingApproval pending) : Exception(
    $"flow suspended at step '{pending.StepId}': awaiting approval")
{
    public PendingApproval Pending { get; } = pending;
}

/// <summary>Engine-emitted event for streaming runs.</summary>
public sealed record FlowStreamEvent(string Type, JsonNode? Data)
{
    public const string StepStart = "flow_step_start";
    public const string StepEnd = "flow_step_end";
    /// <summary>A step skipped on a resumed run (its output was restored).</summary>
    public const string StepSkip = "flow_step_skip";
    /// <summary>Emitted when a run suspends on an approval step.</summary>
    public const string Waiting = "flow_waiting_approval";
    public const string Done = "done";
    public const string Error = "error";
}

/// <summary>One executed step's outcome (engine-internal; mapped to
/// <c>FlowStepResultDto</c> at the boundary).</summary>
public sealed class FlowStepResult
{
    public required string StepId { get; init; }
    public required string StepType { get; init; }
    public string? Name { get; init; }
    /// <summary>done | failed | skipped.</summary>
    public required string Status { get; set; }
    public JsonNode? Output { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    /// <summary>Total handler executions (1 + retries; F3).</summary>
    public int Attempts { get; set; } = 1;
}

/// <summary>Run-level outcome.</summary>
public sealed class FlowRunResult
{
    public required string Status { get; init; } // done | failed | waiting_approval
    public JsonNode? Output { get; init; }
    public string? Error { get; init; }
    public required List<FlowStepResult> Steps { get; init; }
    public required long DurationMs { get; init; }
    /// <summary>Set on a waiting_approval run — the gate FlowService turns
    /// into a ToolApproval + persisted resume state.</summary>
    public PendingApproval? Pending { get; init; }
    /// <summary>Run state captured at suspension — FlowService stores it on
    /// the FlowRun row so a later resume can rebuild the context.</summary>
    public FlowResumeState? ResumeState { get; init; }
}

/// <summary>Execution limits, bound from <c>Flow:*</c> configuration.</summary>
public sealed class FlowLimits
{
    public const int DefaultMaxSteps = 64;
    public const int DefaultMaxIterations = 500;
    public const int DefaultStepTimeoutSeconds = 120;
    public const int DefaultMaxOutputBytes = 256 * 1024;
    public const int DefaultMaxDepth = 3;
    public const int DefaultMaxHttpBodyBytes = 1024 * 1024;

    public int MaxSteps { get; init; } = DefaultMaxSteps;
    public int MaxIterations { get; init; } = DefaultMaxIterations;
    public int StepTimeoutSeconds { get; init; } = DefaultStepTimeoutSeconds;
    public int MaxOutputBytes { get; init; } = DefaultMaxOutputBytes;
    public int MaxDepth { get; init; } = DefaultMaxDepth;
    public int MaxHttpBodyBytes { get; init; } = DefaultMaxHttpBodyBytes;

    public static FlowLimits FromConfiguration(IConfiguration config) => new()
    {
        MaxSteps = config.GetValue("Flow:MaxSteps", DefaultMaxSteps),
        MaxIterations = config.GetValue("Flow:MaxIterations", DefaultMaxIterations),
        StepTimeoutSeconds = config.GetValue("Flow:StepTimeoutSeconds", DefaultStepTimeoutSeconds),
        MaxOutputBytes = config.GetValue("Flow:MaxOutputBytes", DefaultMaxOutputBytes),
        MaxDepth = config.GetValue("Flow:MaxDepth", DefaultMaxDepth),
        MaxHttpBodyBytes = config.GetValue("Flow:MaxHttpBodyBytes", DefaultMaxHttpBodyBytes),
    };
}

/// <summary>Ambient flow-call depth (flow → tool step → flow_&lt;slug&gt; → …).
/// AsyncLocal so nested runs inherit the caller's depth across await chains.</summary>
public static class FlowDepth
{
    private static readonly AsyncLocal<int> Current = new();
    public static int Depth => Current.Value;

    public static IDisposable Push()
    {
        Current.Value++;
        return new PopOnDispose();
    }

    private sealed class PopOnDispose : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (!_done) { Current.Value--; _done = true; }
        }
    }
}
