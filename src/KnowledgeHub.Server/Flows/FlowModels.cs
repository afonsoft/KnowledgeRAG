using System.Text.Json.Nodes;

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
}

/// <summary>Engine-emitted event for streaming runs.</summary>
public sealed record FlowStreamEvent(string Type, JsonNode? Data)
{
    public const string StepStart = "flow_step_start";
    public const string StepEnd = "flow_step_end";
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
}

/// <summary>Run-level outcome.</summary>
public sealed class FlowRunResult
{
    public required string Status { get; init; } // done | failed
    public JsonNode? Output { get; init; }
    public string? Error { get; init; }
    public required List<FlowStepResult> Steps { get; init; }
    public required long DurationMs { get; init; }
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
