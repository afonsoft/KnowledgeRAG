using System.Text.Json;
using System.Text.Json.Nodes;
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
        var queue = new Queue<FlowStepDto>(definition.Steps);
        var count = 0;
        while (queue.Count > 0)
        {
            var step = queue.Dequeue();
            count++;
            if (count > FlowLimits.DefaultMaxSteps * 4)
                return $"definition too large (> {FlowLimits.DefaultMaxSteps * 4} steps incl. nested)";
            if (string.IsNullOrWhiteSpace(step.Id))
                return "every step needs a non-empty 'id'";
            if (!seen.Add(step.Id))
                return $"duplicate step id '{step.Id}'";
            if (!KnownTypes.Contains(step.Type))
                return $"unknown step type '{step.Type}' (step '{step.Id}')";
            foreach (var nested in step.Steps ?? [])
                queue.Enqueue(nested);
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
                            if (nested is not null) queue.Enqueue(nested);
                        }
                    }
                }
            }
            if (step.Config?.TryGetPropertyValue("else", out var el) == true && el is JsonArray elseArr)
            {
                foreach (var nestedNode in elseArr)
                {
                    var nested = nestedNode?.Deserialize<FlowStepDto>(SharedJson.Options);
                    if (nested is not null) queue.Enqueue(nested);
                }
            }
        }
        return null;
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tool", "knowledge", "llm", "http", "condition", "foreach", "transform", "output", "fail",
    };

    /// <summary>Run a flow by id or slug; executes the engine and persists the
    /// audit record. Throws <see cref="FlowAbortException"/> on depth overflow.</summary>
    public async Task<FlowRunResultDto> RunAsync(
        AgentFlow flow,
        JsonObject? inputs,
        IServiceProvider services,
        Guid? apiKeyId,
        Func<FlowStreamEvent, CancellationToken, ValueTask>? sink,
        CancellationToken ct)
    {
        if (FlowDepth.Depth >= FlowLimits.DefaultMaxDepth)
            throw new FlowAbortException(
                $"flow recursion limit ({FlowLimits.DefaultMaxDepth}) — '{flow.Slug}' cannot run inside this call chain");

        var definition = DeserializeDefinition(flow);
        using var depth = FlowDepth.Push();
        var result = await engine.ExecuteAsync(definition, inputs, services, sink, ct);

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
        };
        db.FlowRuns.Add(run);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "flow {Slug} run {RunId} {Status} in {Ms}ms ({Steps} steps)",
            flow.Slug, run.Id, run.Status, run.DurationMs, result.Steps.Count);

        return new FlowRunResultDto(
            run.Id, result.Status, result.Output, result.Error,
            result.Steps.Select(ToDto).ToList(), result.DurationMs);
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
            run.Error, steps, run.DurationMs ?? 0);
    }

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
        new(r.StepId, r.StepType, r.Name, r.Status, r.Output, r.Error, r.DurationMs);
}
