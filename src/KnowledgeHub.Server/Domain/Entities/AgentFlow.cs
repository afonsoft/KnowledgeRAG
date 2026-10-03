namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>User-defined agent flow (SPEC agent-flows): a deterministic
/// orchestration pipeline composed in the admin UI and exposed to the agent
/// loop/MCP as a `flow_<slug>` tool. Definition is a serialized
/// <c>FlowDefinitionDto</c>.</summary>
public sealed class AgentFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>MCP-safe slug (<see cref="Mcp.ToolSlugger"/>), unique across flows.</summary>
    public string Slug { get; set; } = "";
    /// <summary>Tool-facing description — the LLM uses it to decide when to
    /// invoke the flow (AnythingLLM flowInfo semantics).</summary>
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Bumped on every definition update; recorded on runs.</summary>
    public int Version { get; set; } = 1;
    public string DefinitionJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Audit record for a flow execution (same pattern as EvalRun /
/// ApiKeyUsageEvent) — inputs, per-step trace, output, duration, caller key.</summary>
public sealed class FlowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    /// <summary>Definition version this run executed.</summary>
    public int FlowVersion { get; set; }
    /// <summary>running | done | failed | waiting_approval.</summary>
    public string Status { get; set; } = "running";
    /// <summary>Resolved input values (post defaulting).</summary>
    public string? InputsJson { get; set; }
    /// <summary>Serialized per-step <c>FlowStepResultDto</c> trace.</summary>
    public string? StepResultsJson { get; set; }
    /// <summary>Final output value.</summary>
    public string? OutputJson { get; set; }
    public string? Error { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>API key that triggered the run (null for cookie admin).</summary>
    public Guid? ApiKeyId { get; set; }
    /// <summary>Trigger that dispatched this run (null = manual/agent).</summary>
    public Guid? TriggerId { get; set; }
    /// <summary>Approval suspending the run (HITL gate); cleared on resume.</summary>
    public Guid? PendingApprovalId { get; set; }
    /// <summary>Serialized FlowResumeState used to continue after approval.</summary>
    public string? ResumeStateJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Event source that starts a flow run — an incoming webhook
/// (unguessable token in the path) or a poll-interval schedule dispatched
/// by the FlowSchedulerWorker.</summary>
public sealed class FlowTrigger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    /// <summary>"webhook" | "schedule".</summary>
    public string Kind { get; set; } = "webhook";
    public bool Enabled { get; set; } = true;
    /// <summary>Webhook: opaque bearer token in the URL path
    /// (/api/flowtriggers/{token}). Generated at creation with a
    /// <c>fwt_</c> prefix so the Serilog enricher masks it in request logs.</summary>
    public string? Secret { get; set; }
    /// <summary>Schedule: poll interval in seconds (min 60). Deduped by
    /// <see cref="LastFiredAt"/>.</summary>
    public int? IntervalSeconds { get; set; }
    /// <summary>{ inputs: {...} } — static inputs merged under the event
    /// payload for webhooks, used as-is (plus trigger metadata) for
    /// schedules.</summary>
    public string? ConfigJson { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
