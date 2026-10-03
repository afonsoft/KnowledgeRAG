using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Api;
using Microsoft.AspNetCore.Http;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Flows;

/// <summary>
/// Flow CRUD + execution boundary: wraps <see cref="IFlowEngine"/>, persists
/// <see cref="FlowRun"/> audits, enforces the depth cap, and invalidates the
/// tool catalog when the flow set changes (a saved flow is a catalog tool).
/// </summary>
public sealed class FlowService(
    KnowledgeHubDbContext db,
    IFlowEngine engine,
    IToolCatalogChangeNotifier notifier,
    ILogger<FlowService> logger)
{
    public async Task<List<FlowDto>> ListAsync(bool enabledOnly, CancellationToken ct) =>
        await db.AgentFlows
            .Where(f => !enabledOnly || f.Enabled)
            .OrderBy(f => f.Name)
            .Select(f => new FlowDto(f.Id, f.Name, f.Slug, f.Description, f.Enabled, f.Version, f.UpdatedAt))
            .ToListAsync(ct);

    public async Task<FlowDetailDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var flow = await db.AgentFlows.FindAsync([id], ct);
        return flow is null ? null : ToDetail(flow);
    }

    public async Task<FlowDetailDto?> GetBySlugAsync(string slug, CancellationToken ct)
    {
        var flow = await db.AgentFlows.FirstOrDefaultAsync(f => f.Slug == slug, ct);
        return flow is null ? null : ToDetail(flow);
    }

    /// <summary>Create; returns null when the derived slug collides.</summary>
    public async Task<FlowDetailDto?> CreateAsync(CreateFlowRequest request, CancellationToken ct)
    {
        var error = ValidateDefinition(request.Definition);
        if (error is not null)
            throw new FlowStepException(error);

        var slug = await UniqueSlugAsync(request.Name, excludeId: null, ct);
        var flow = new AgentFlow
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Description = request.Description?.Trim(),
            Enabled = true,
            Version = 1,
            DefinitionJson = SerializeDefinition(request.Definition),
        };
        db.AgentFlows.Add(flow);
        await db.SaveChangesAsync(ct);
        await notifier.NotifyToolsChangedAsync(ct);
        return ToDetail(flow);
    }

    /// <summary>Update metadata/definition; bumps Version on definition change.
    /// Slug is stable across renames (renaming a flow would silently break
    /// agent prompts that cite <c>flow_&lt;slug&gt;</c>).</summary>
    public async Task<FlowDetailDto?> UpdateAsync(Guid id, UpdateFlowRequest request, CancellationToken ct)
    {
        var flow = await db.AgentFlows.FindAsync([id], ct);
        if (flow is null)
            return null;

        if (request.Name is { } name && !string.IsNullOrWhiteSpace(name))
            flow.Name = name.Trim();
        if (request.Description is not null)
            flow.Description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim();
        if (request.Enabled is { } enabled)
            flow.Enabled = enabled;
        if (request.Definition is { } definition)
        {
            var error = ValidateDefinition(definition);
            if (error is not null)
                throw new FlowStepException(error);
            flow.DefinitionJson = SerializeDefinition(definition);
            flow.Version++;
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await notifier.NotifyToolsChangedAsync(ct);
        return ToDetail(flow);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var flow = await db.AgentFlows.FindAsync([id], ct);
        if (flow is null)
            return false;
        db.AgentFlows.Remove(flow);
        await db.SaveChangesAsync(ct);
        await notifier.NotifyToolsChangedAsync(ct);
        return true;
    }

    /// <summary>Validate a definition without running it — structure +
    /// duplicate ids + unknown step types.</summary>
    public string? ValidateDefinition(FlowDefinitionDto definition)
    {
        if (definition.Steps.Count == 0)
            return "definition must contain at least one step";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // inForeach tracks whether the step sits under a foreach body —
        // an `approval` gate there could not be resumed faithfully (the
        // engine would re-run the loop and skip every iteration's steps).
        var queue = new Queue<(FlowStepDto Step, bool InForeach)>();
        foreach (var s in definition.Steps)
            queue.Enqueue((s, false));
        var count = 0;
        while (queue.Count > 0)
        {
            var (step, inForeach) = queue.Dequeue();
            count++;
            if (count > FlowLimits.DefaultMaxSteps * 4)
                return $"definition too large (> {FlowLimits.DefaultMaxSteps * 4} steps incl. nested)";
            if (string.IsNullOrWhiteSpace(step.Id))
                return "every step needs a non-empty 'id'";
            if (!seen.Add(step.Id))
                return $"duplicate step id '{step.Id}'";
            if (!KnownTypes.Contains(step.Type))
                return $"unknown step type '{step.Type}' (step '{step.Id}')";
            if (inForeach && step.Type == "approval")
                return $"approval step '{step.Id}' cannot be nested inside foreach (loop state is not resumable)";
            var nestedForeach = inForeach || step.Type == "foreach";
            foreach (var nested in step.Steps ?? [])
                queue.Enqueue((nested, nestedForeach));
            // condition branches live in config
            if (step.Config?.TryGetPropertyValue("branches", out var br) == true && br is JsonArray arr)
            {
                foreach (var branch in arr)
                {
                    if (branch is JsonObject b && b["steps"] is JsonArray steps)
                    {
                        foreach (var nestedNode in steps)
                        {
                            var nested = nestedNode?.Deserialize<FlowStepDto>(SharedJson.Options);
                            if (nested is not null) queue.Enqueue((nested, inForeach));
                        }
                    }
                }
            }
            if (step.Config?.TryGetPropertyValue("else", out var el) == true && el is JsonArray elseArr)
            {
                foreach (var nestedNode in elseArr)
                {
                    var nested = nestedNode?.Deserialize<FlowStepDto>(SharedJson.Options);
                    if (nested is not null) queue.Enqueue((nested, inForeach));
                }
            }
        }
        return null;
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tool", "knowledge", "llm", "http", "condition", "foreach", "transform", "output", "fail", "approval",
    };

    /// <summary>Run a flow by id or slug; executes the engine and persists the
    /// audit record. Throws <see cref="FlowAbortException"/> on depth overflow.</summary>
    public async Task<FlowRunResultDto> RunAsync(
        AgentFlow flow,
        JsonObject? inputs,
        IServiceProvider services,
        Guid? apiKeyId,
        Func<FlowStreamEvent, CancellationToken, ValueTask>? sink,
        CancellationToken ct,
        Guid? triggerId = null)
    {
        if (FlowDepth.Depth >= FlowLimits.DefaultMaxDepth)
            throw new FlowAbortException(
                $"flow recursion limit ({FlowLimits.DefaultMaxDepth}) — '{flow.Slug}' cannot run inside this call chain");

        var definition = DeserializeDefinition(flow);
        using var depth = FlowDepth.Push();
        var result = await engine.ExecuteAsync(definition, inputs, services, sink, null, ct);

        var run = new FlowRun
        {
            FlowId = flow.Id,
            FlowVersion = flow.Version,
            Status = result.Status,
            InputsJson = inputs?.ToJsonString(),
            StepResultsJson = JsonSerializer.Serialize(
                result.Steps.Select(ToDto).ToList(), SharedJson.Options),
            OutputJson = result.Output?.ToJsonString(),
            Error = result.Error,
            DurationMs = result.DurationMs,
            ApiKeyId = apiKeyId,
            TriggerId = triggerId,
        };
        db.FlowRuns.Add(run);

        if (result is { Status: "waiting_approval", Pending: { } pending, ResumeState: { } resume })
        {
            var approval = new ToolApproval
            {
                ToolName = $"flow:{flow.Slug}:{pending.StepId}",
                ArgumentsJson = SerializeResumeArgs(flow, pending, run.Id),
                RequestedBy = "flow",
                Status = "pending",
                StateJson = run.Id.ToString(),
            };
            db.Approvals.Add(approval);
            run.PendingApprovalId = approval.Id;
            run.ResumeStateJson = JsonSerializer.Serialize(resume, resumeJson);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "flow {Slug} run {RunId} {Status} in {Ms}ms ({Steps} steps)",
            flow.Slug, run.Id, run.Status, run.DurationMs, result.Steps.Count);

        return new FlowRunResultDto(
            run.Id, result.Status, result.Output, result.Error,
            result.Steps.Select(ToDto).ToList(), result.DurationMs)
        { ApprovalId = run.PendingApprovalId };
    }

    public async Task<List<FlowRunDto>> ListRunsAsync(Guid flowId, int take, CancellationToken ct) =>
        // EF Core + SQLite cannot ORDER BY DateTimeOffset and JsonNode.Parse
        // does not translate — both happen client-side after the projection.
        (await db.FlowRuns
            .Where(r => r.FlowId == flowId)
            .Select(r => new
            {
                r.Id,
                r.FlowId,
                r.FlowVersion,
                r.Status,
                r.InputsJson,
                r.OutputJson,
                r.Error,
                r.DurationMs,
                r.CreatedAt,
            })
            .ToListAsync(ct))
        .OrderByDescending(r => r.CreatedAt)
        .Take(Math.Clamp(take, 1, 200))
        .Select(r => new FlowRunDto(r.Id, r.FlowId, r.FlowVersion, r.Status,
            r.InputsJson == null ? null : (JsonObject)JsonNode.Parse(r.InputsJson)!,
            r.OutputJson == null ? null : JsonNode.Parse(r.OutputJson),
            r.Error, r.DurationMs, r.CreatedAt))
        .ToList();

    public async Task<FlowRunResultDto?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.FlowRuns.FindAsync([runId], ct);
        if (run is null)
            return null;
        var steps = run.StepResultsJson is null
            ? []
            : JsonSerializer.Deserialize<List<FlowStepResultDto>>(run.StepResultsJson, SharedJson.Options) ?? [];
        return new FlowRunResultDto(
            run.Id, run.Status,
            run.OutputJson is null ? null : JsonNode.Parse(run.OutputJson),
            run.Error, steps, run.DurationMs ?? 0)
        { ApprovalId = run.PendingApprovalId };
    }

    /// <summary>Serializer for the server-only resume state — JsonNode-heavy,
    /// reflection-based (the Shared source-gen context cannot see server
    /// types; WASM reflection restrictions don't apply here).</summary>
    private static readonly JsonSerializerOptions resumeJson = new(JsonSerializerDefaults.Web);

    private static string SerializeResumeArgs(AgentFlow flow, PendingApproval pending, Guid runId) =>
        new JsonObject
        {
            ["flow"] = flow.Slug,
            ["stepId"] = pending.StepId,
            ["message"] = pending.Message,
            ["runId"] = runId.ToString(),
        }.ToJsonString();

    /// <summary>Resume a run suspended on an approval step. The approval must
    /// already be resolved — approved seeds the step output with the
    /// resolution; denied fails the run. Throws <see cref="ConflictException"/>
    /// while the approval is still pending or already consumed.</summary>
    /// <param name="services">A real request scope — resumed steps resolve
    /// catalog/chat/secret services through it exactly like a fresh run.</param>
    public async Task<FlowRunResultDto> ResumeByApprovalAsync(
        Guid approvalId, IServiceProvider services, CancellationToken ct)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct)
            ?? throw new KeyNotFoundException($"approval '{approvalId}' not found");
        if (approval.RequestedBy != "flow")
            throw new ConflictException($"approval '{approvalId}' is not a flow gate");
        if (approval.Status == "pending")
            throw new ConflictException($"approval '{approvalId}' is still pending");
        if (approval.ResumedAt is not null)
            throw new ConflictException($"approval '{approvalId}' already resumed");

        var run = await db.FlowRuns.FirstOrDefaultAsync(r => r.PendingApprovalId == approvalId, ct)
            ?? throw new KeyNotFoundException($"no flow run for approval '{approvalId}'");
        var flow = await db.AgentFlows.FindAsync([run.FlowId], ct)
            ?? throw new InvalidOperationException($"flow of run '{run.Id}' is gone");

        // Claim the resume atomically — same race guard as the agent loop.
        var claimed = await db.Approvals
            .Where(a => a.Id == approvalId && a.ResumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResumedAt, DateTimeOffset.UtcNow), ct);
        if (claimed == 0)
            throw new ConflictException($"approval '{approvalId}' already resumed");
        approval.ResumedAt = DateTimeOffset.UtcNow;

        if (approval.Status != "approved")
        {
            run.Status = "failed";
            run.Error = $"approval denied (step '{approval.ToolName}')";
            await db.SaveChangesAsync(ct);
            return new FlowRunResultDto(run.Id, "failed", null, run.Error, [], run.DurationMs ?? 0);
        }

        var state = JsonSerializer.Deserialize<FlowResumeState>(run.ResumeStateJson ?? "{}", resumeJson)
            ?? throw new InvalidOperationException($"run '{run.Id}' has no resume state");

        // Seed the gate's resolution as the step output so the engine skips
        // it (and downstream steps can read steps.<id>.output.resolved).
        if (state.PendingStepId is { } pid)
            state.StepOutputs![pid] = new JsonObject
            {
                ["resolved"] = true,
                ["approved"] = approval.Status == "approved",
            };
        state.PendingStepId = null;

        var definition = DeserializeDefinition(flow);
        using var depth = FlowDepth.Push();
        var result = await engine.ExecuteAsync(definition, null, services, null, state, ct);

        run.Status = result.Status;
        run.StepResultsJson = JsonSerializer.Serialize(result.Steps.Select(ToDto).ToList(), SharedJson.Options);
        run.OutputJson = result.Output?.ToJsonString();
        run.Error = result.Error;
        run.DurationMs = (run.DurationMs ?? 0) + result.DurationMs;
        run.ResumeStateJson = null;
        run.PendingApprovalId = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("flow {Slug} run {RunId} resumed → {Status}",
            flow.Slug, run.Id, run.Status);
        return new FlowRunResultDto(run.Id, result.Status, result.Output, result.Error,
            result.Steps.Select(ToDto).ToList(), result.DurationMs);
    }

    // ── Triggers ──────────────────────────────────────────────────────

    public async Task<List<FlowTriggerDto>> ListTriggersAsync(Guid flowId, HttpRequest request, CancellationToken ct) =>
        // SQLite can't ORDER BY DateTimeOffset — sort client-side.
        (await db.FlowTriggers
            .Where(t => t.FlowId == flowId)
            .ToListAsync(ct))
        .OrderBy(t => t.CreatedAt)
        .Select(t => ToTriggerDto(t, request)).ToList();

    /// <summary>Create a trigger; returns null when the flow is gone, throws
    /// <see cref="FlowStepException"/> on invalid kind/config.</summary>
    public async Task<FlowTriggerDto?> CreateTriggerAsync(
        Guid flowId, CreateFlowTriggerRequest request, HttpRequest http, CancellationToken ct)
    {
        if (await db.AgentFlows.FindAsync([flowId], ct) is null)
            return null;
        var error = ValidateTrigger(request.Kind, request.IntervalSeconds);
        if (error is not null)
            throw new FlowStepException(error);

        var trigger = new FlowTrigger
        {
            FlowId = flowId,
            Kind = request.Kind.Trim().ToLowerInvariant(),
            IntervalSeconds = request.Kind.Equals("schedule", StringComparison.OrdinalIgnoreCase)
                ? request.IntervalSeconds
                : null,
            Secret = request.Kind.Equals("webhook", StringComparison.OrdinalIgnoreCase)
                ? "fwt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()
                : null,
            ConfigJson = request.Inputs is null ? null : new JsonObject { ["inputs"] = request.Inputs }.ToJsonString(),
        };
        db.FlowTriggers.Add(trigger);
        await db.SaveChangesAsync(ct);
        return ToTriggerDto(trigger, http);
    }

    public async Task<FlowTriggerDto?> UpdateTriggerAsync(
        Guid triggerId, UpdateFlowTriggerRequest request, HttpRequest http, CancellationToken ct)
    {
        var trigger = await db.FlowTriggers.FindAsync([triggerId], ct);
        if (trigger is null)
            return null;
        if (request.Enabled is { } enabled)
            trigger.Enabled = enabled;
        if (request.IntervalSeconds is { } interval)
        {
            if (trigger.Kind == "schedule" && interval < 60)
                throw new FlowStepException("schedule interval must be ≥ 60 seconds");
            trigger.IntervalSeconds = interval;
        }
        if (request.Inputs is not null)
            trigger.ConfigJson = new JsonObject { ["inputs"] = request.Inputs }.ToJsonString();
        await db.SaveChangesAsync(ct);
        return ToTriggerDto(trigger, http);
    }

    public async Task<bool> DeleteTriggerAsync(Guid triggerId, CancellationToken ct)
    {
        var trigger = await db.FlowTriggers.FindAsync([triggerId], ct);
        if (trigger is null)
            return false;
        db.FlowTriggers.Remove(trigger);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Fire a webhook trigger by its secret token. Returns the run
    /// result (202-style: the run may still be waiting_approval).</summary>
    public async Task<FlowRunResultDto?> InvokeWebhookAsync(
        string token, JsonObject? body, IServiceProvider services, CancellationToken ct)
    {
        var trigger = await db.FlowTriggers
            .FirstOrDefaultAsync(t => t.Secret == token && t.Enabled && t.Kind == "webhook", ct);
        if (trigger is null)
            return null;
        var flow = await db.AgentFlows.FirstOrDefaultAsync(f => f.Id == trigger.FlowId && f.Enabled, ct);
        if (flow is null)
            throw new FlowStepException("the target flow is disabled or gone");

        var inputs = StaticInputs(trigger);
        inputs["trigger"] = new JsonObject
        {
            ["kind"] = "webhook",
            ["triggerId"] = trigger.Id.ToString(),
            ["receivedAt"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        if (body is not null)
            inputs["event"] = body.DeepClone();

        trigger.LastFiredAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await RunAsync(flow, inputs, services, apiKeyId: null, sink: null, ct, triggerId: trigger.Id);
    }

    /// <summary>Fire a schedule trigger — called by FlowSchedulerWorker once
    /// the interval elapses. Same merge semantics as the webhook path minus
    /// the event body.</summary>
    public async Task<FlowRunResultDto?> FireScheduleAsync(
        FlowTrigger trigger, IServiceProvider services, CancellationToken ct)
    {
        var flow = await db.AgentFlows.FirstOrDefaultAsync(f => f.Id == trigger.FlowId && f.Enabled, ct);
        if (flow is null)
            return null;
        var inputs = StaticInputs(trigger);
        inputs["trigger"] = new JsonObject
        {
            ["kind"] = "schedule",
            ["triggerId"] = trigger.Id.ToString(),
            ["firedAt"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        return await RunAsync(flow, inputs, services, apiKeyId: null, sink: null, ct, triggerId: trigger.Id);
    }

    /// <summary>Due schedule triggers (enabled, interval elapsed).</summary>
    public async Task<List<FlowTrigger>> DueSchedulesAsync(DateTimeOffset now, CancellationToken ct) =>
        // EF+SQLite can't compare DateTimeOffset in some versions — the
        // trigger set is small; evaluate in memory.
        (await db.FlowTriggers
            .Where(t => t.Enabled && t.Kind == "schedule" && t.IntervalSeconds != null)
            .ToListAsync(ct))
        .Where(t => t.LastFiredAt is null
            || t.LastFiredAt.Value.AddSeconds(t.IntervalSeconds!.Value) <= now)
        .ToList();

    private static JsonObject StaticInputs(FlowTrigger trigger)
    {
        var inputs = new JsonObject();
        if (trigger.ConfigJson is { } json
            && JsonNode.Parse(json) is JsonObject cfg
            && cfg["inputs"] is JsonObject declared)
        {
            foreach (var (k, v) in declared)
                inputs[k] = v?.DeepClone();
        }
        return inputs;
    }

    private static string? ValidateTrigger(string? kind, int? intervalSeconds) => kind?.Trim().ToLowerInvariant() switch
    {
        "webhook" => null,
        "schedule" when intervalSeconds is >= 60 => null,
        "schedule" => "schedule triggers require intervalSeconds ≥ 60",
        _ => $"kind must be 'webhook' or 'schedule' (got '{kind}')",
    };

    private FlowTriggerDto ToTriggerDto(FlowTrigger t, HttpRequest http) => new(
        t.Id, t.FlowId, t.Kind, t.Enabled,
        t.Secret is null ? null : $"{http.Scheme}://{http.Host}/api/flowtriggers/{t.Secret}",
        t.IntervalSeconds, t.LastFiredAt, t.CreatedAt);

    internal static FlowDetailDto ToDetail(AgentFlow flow) => new(
        new FlowDto(flow.Id, flow.Name, flow.Slug, flow.Description, flow.Enabled, flow.Version, flow.UpdatedAt),
        DeserializeDefinition(flow));

    internal static FlowDefinitionDto DeserializeDefinition(AgentFlow flow) =>
        JsonSerializer.Deserialize<FlowDefinitionDto>(flow.DefinitionJson, SharedJson.Options)
            ?? new FlowDefinitionDto([], []);

    private static string SerializeDefinition(FlowDefinitionDto definition) =>
        JsonSerializer.Serialize(definition, SharedJson.Options);

    private async Task<string> UniqueSlugAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var baseSlug = ToolSlugger.Slugify(name);
        var slug = baseSlug;
        var suffix = 1;
        while (await db.AgentFlows.AnyAsync(
                   f => f.Slug == slug && f.Id != excludeId, ct))
            slug = $"{baseSlug}_{++suffix}";
        return slug;
    }

    private static FlowStepResultDto ToDto(FlowStepResult r) =>
        new(r.StepId, r.StepType, r.Name, r.Status, r.Output, r.Error, r.DurationMs)
        { Attempts = r.Attempts };
}
