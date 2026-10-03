using System.Text.Json.Nodes;

namespace KnowledgeHub.Shared.Contracts;

/// <summary>A declared flow input (the `start` block) — drives the generated
/// `flow_<slug>` MCP inputSchema and REST validation.</summary>
public sealed record FlowInputDto(
    string Name,
    string Type,
    bool Required,
    string? Description,
    JsonNode? Default);

/// <summary>One step inside a flow definition. Nested steps (foreach bodies,
/// condition branches) live in <see cref="Steps"/> or inside
/// <see cref="Config"/> (`branches[].steps`), parsed with the same shape.</summary>
public sealed record FlowStepDto(
    string Id,
    string Type,
    string? Name,
    JsonObject? Config,
    List<FlowStepDto>? Steps);

/// <summary>Flow definition: declared inputs + ordered step list.</summary>
public sealed record FlowDefinitionDto(
    List<FlowInputDto> Inputs,
    List<FlowStepDto> Steps);

/// <summary>Flow list item.</summary>
public sealed record FlowDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    bool Enabled,
    int Version,
    DateTimeOffset UpdatedAt);

/// <summary>Flow + definition.</summary>
public sealed record FlowDetailDto(
    FlowDto Flow,
    FlowDefinitionDto Definition);

/// <summary>Create flow. Slug derived from Name server-side.</summary>
public sealed record CreateFlowRequest(
    string Name,
    string? Description,
    FlowDefinitionDto Definition);

/// <summary>Update flow; null members keep their current values.</summary>
public sealed record UpdateFlowRequest(
    string? Name,
    string? Description,
    bool? Enabled,
    FlowDefinitionDto? Definition);

/// <summary>Run a flow with concrete input values.</summary>
public sealed record FlowRunRequest(
    JsonObject? Inputs);

/// <summary>POST /api/flows/validate body — structure check without running.</summary>
public sealed record ValidateFlowRequest(
    FlowDefinitionDto Definition);

/// <summary>Per-step execution record inside a run result.</summary>
public sealed record FlowStepResultDto(
    string StepId,
    string StepType,
    string? Name,
    string Status,
    JsonNode? Output,
    string? Error,
    long DurationMs)
{
    /// <summary>Attempts the step needed (config.retry), 1 when it first-try
    /// succeeded or the step doesn't retry.</summary>
    public int Attempts { get; init; } = 1;
}

/// <summary>Outcome of a flow run (REST + MCP share this shape).</summary>
public sealed record FlowRunResultDto(
    Guid RunId,
    string Status,
    JsonNode? Output,
    string? Error,
    List<FlowStepResultDto> Steps,
    long DurationMs)
{
    /// <summary>Approval suspending the run (Status="waiting_approval") —
    /// resolve it via /api/approvals; the flow then resumes.</summary>
    public Guid? ApprovalId { get; init; }
}

/// <summary>Persisted run (audit listing).</summary>
public sealed record FlowRunDto(
    Guid Id,
    Guid FlowId,
    int FlowVersion,
    string Status,
    JsonObject? Inputs,
    JsonNode? Output,
    string? Error,
    long? DurationMs,
    DateTimeOffset CreatedAt);

/// <summary>Event source that starts a flow run (webhook or schedule).</summary>
public sealed record FlowTriggerDto(
    Guid Id,
    Guid FlowId,
    string Kind,
    bool Enabled,
    /// <summary>Full webhook URL (kind=webhook); null for schedules.</summary>
    string? WebhookUrl,
    int? IntervalSeconds,
    DateTimeOffset? LastFiredAt,
    DateTimeOffset CreatedAt);

/// <summary>Create a flow trigger. Kind: "webhook" | "schedule".
/// schedule requires IntervalSeconds ≥ 60; webhook generates the secret
/// server-side (returned once in the DTO's WebhookUrl).</summary>
public sealed record CreateFlowTriggerRequest(
    string Kind,
    int? IntervalSeconds,
    JsonObject? Inputs);

/// <summary>Update a trigger; null members keep their current values.</summary>
public sealed record UpdateFlowTriggerRequest(
    bool? Enabled,
    int? IntervalSeconds,
    JsonObject? Inputs);
