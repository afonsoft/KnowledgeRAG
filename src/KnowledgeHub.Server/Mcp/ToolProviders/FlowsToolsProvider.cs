using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Flows;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Agent-flow tools (SPEC agent-flows): <c>list_flows</c>,
/// <c>run_flow</c>, and every enabled flow re-exported as a
/// <c>flow_&lt;slug&gt;</c> tool — the AnythingLLM model: a published flow
/// becomes a first-class catalog tool the agent loop calls like any other.
/// Built per tools/list call from live DB state, so saving/enabling a flow in
/// the UI immediately mutates the catalog.
/// </summary>
public sealed class FlowsToolsProvider(
    IServiceProvider providerScope) : IToolProvider
{
    private static readonly JsonObject ListFlowsSchema = JsonNode.Parse("""
        {"type":"object","properties":{},"additionalProperties":false,"examples":[{}]}
        """)!.AsObject();

    private static readonly JsonObject RunFlowSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "flow":   { "type": "string", "description": "Flow slug (flow_<slug>) or GUID", "examples": ["weekly-digest"] },
            "inputs": { "type": "object", "description": "Input values matching the flow's declared inputs" }
          },
          "required": ["flow"],
          "additionalProperties": false,
          "examples": [{"flow":"weekly-digest","inputs":{"since":"2026-09-20"}}]
        }
        """)!.AsObject();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<Data.KnowledgeHubDbContext>();
        var flows = await db.AgentFlows
            .AsNoTracking()
            .Where(f => f.Enabled)
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);

        // Read-only lookup across the OTHER providers — calling
        // IDynamicToolCatalog here would recurse into this provider.
        var readOnlyByName = await BuildReadOnlyMapAsync(services, cancellationToken);

        var tools = new List<CatalogTool>
        {
            new()
            {
                Name = "list_flows",
                Title = "List Agent Flows",
                Description = "List enabled UI-defined agent flows (slug, description, inputs). " +
                    "Flows are deterministic pipelines callable via run_flow or their flow_<slug> tool.",
                InputSchema = ListFlowsSchema,
                ReadOnly = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
                Handler = (ctx, ct) => ListFlowsAsync(ctx, flows, ct),
            },
            new()
            {
                Name = "run_flow",
                Title = "Run Agent Flow",
                Description = "Execute an enabled agent flow by slug or id. " +
                    "Inputs must match the flow's declared inputs (see list_flows).",
                InputSchema = RunFlowSchema,
                ReadOnly = false,
                DestructiveHint = null,
                IdempotentHint = false,
                OpenWorldHint = true,
                Handler = (ctx, ct) => RunFlowAsync(ctx, ct),
            },
        };

        // Every enabled flow becomes a catalog tool — inputSchema derived
        // straight from the flow's start-block declarations.
        foreach (var flow in flows)
        {
            var captured = flow;
            var definition = FlowService.DeserializeDefinition(flow);
            tools.Add(new CatalogTool
            {
                Name = $"flow_{captured.Slug}",
                Title = captured.Name,
                Description = BuildDescription(captured),
                InputSchema = BuildInputSchema(definition),
                ReadOnly = IsReadOnly(definition, readOnlyByName),
                DestructiveHint = null,
                IdempotentHint = false,
                OpenWorldHint = TouchesOpenWorld(definition),
                Handler = (ctx, ct) => InvokeFlowAsync(ctx, captured, ct),
            });
        }

        return tools;
    }

    private static string BuildDescription(Domain.Entities.AgentFlow flow)
    {
        var desc = string.IsNullOrWhiteSpace(flow.Description)
            ? $"Run the '{flow.Name}' agent flow (v{flow.Version})."
            : flow.Description.Trim();
        return $"{desc} [UI-defined flow]";
    }

    private static JsonObject BuildInputSchema(FlowDefinitionDto definition)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var input in definition.Inputs)
        {
            var prop = new JsonObject { ["type"] = MapType(input.Type) };
            if (!string.IsNullOrWhiteSpace(input.Description))
                prop["description"] = input.Description;
            if (input.Default is not null)
                prop["default"] = input.Default.DeepClone();
            properties[input.Name] = prop;
            if (input.Required && input.Default is null)
                required.Add(input.Name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static string MapType(string type) => type.ToLowerInvariant() switch
    {
        "number" or "integer" or "int" or "double" or "float" => "number",
        "bool" or "boolean" => "boolean",
        "object" => "object",
        "array" or "list" => "array",
        _ => "string",
    };

    private async Task<Dictionary<string, bool>> BuildReadOnlyMapAsync(
        IServiceProvider services, CancellationToken ct)
    {
        // Resolved lazily: injecting IEnumerable<IToolProvider> into a provider
        // creates a DI circularity (we are ourselves an IToolProvider).
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providerScope.GetServices<IToolProvider>())
        {
            if (provider is FlowsToolsProvider)
                continue;
            try
            {
                foreach (var tool in await provider.GetToolsAsync(services, ct))
                    map[tool.Name] = tool.ReadOnly;
            }
            catch
            {
                // provider failure → its tools resolve to non-readonly (safe default)
            }
        }
        return map;
    }

    /// <summary>A flow is read-only only when every tool/knowledge step it
    /// declares resolves to a read-only catalog tool and it has no http step.
    /// Unknown tool names resolve to non-readonly (safe default).</summary>
    private static bool IsReadOnly(FlowDefinitionDto def, IReadOnlyDictionary<string, bool> readOnlyByName)
    {
        var hasHttp = def.Steps.Any(s => s.Type == "http");
        if (hasHttp)
            return false;
        return ReferencedToolNames(def).All(
            name => readOnlyByName.TryGetValue(name, out var ro) && ro);
    }

    private static bool TouchesOpenWorld(FlowDefinitionDto def) =>
        def.Steps.Any(s => s.Type is "http" or "tool");

    /// <summary>Tool names referenced by tool/knowledge steps (top level only;
    /// nested steps inside condition/foreach count too).</summary>
    private static IEnumerable<string> ReferencedToolNames(FlowDefinitionDto def)
    {
        var stack = new Stack<FlowStepDto>(def.Steps);
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            if (s.Type is "tool" or "knowledge")
            {
                if (s.Config?.TryGetPropertyValue("tool", out var t) == true
                    && t is JsonValue jv && jv.TryGetValue<string>(out var name))
                    yield return name;
                else if (s.Type == "knowledge")
                    yield return "search_knowledge";
            }
            foreach (var nested in s.Steps ?? [])
                stack.Push(nested);
            if (s.Config?.TryGetPropertyValue("branches", out var br) == true && br is JsonArray arr)
            {
                foreach (var b in arr)
                {
                    if (b is JsonObject bo && bo["steps"] is JsonArray steps)
                    {
                        foreach (var node in steps)
                        {
                            var nested = node?.Deserialize<FlowStepDto>(Shared.SharedJson.Options);
                            if (nested is not null) stack.Push(nested);
                        }
                    }
                }
            }
            if (s.Config?.TryGetPropertyValue("else", out var el) == true && el is JsonArray elseArr)
            {
                foreach (var node in elseArr)
                {
                    var nested = node?.Deserialize<FlowStepDto>(Shared.SharedJson.Options);
                    if (nested is not null) stack.Push(nested);
                }
            }
        }
    }

    private static async ValueTask<CallToolResult> ListFlowsAsync(
        ToolCallContext ctx, List<Domain.Entities.AgentFlow> flows, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<Data.KnowledgeHubDbContext>();
        var items = await db.AgentFlows.AsNoTracking()
            .Where(f => f.Enabled)
            .OrderBy(f => f.Name)
            .Select(f => new
            {
                f.Id,
                f.Name,
                f.Slug,
                f.Description,
                f.Version,
                tool = "flow_" + f.Slug,
            })
            .ToListAsync(ct);

        var text = items.Count == 0
            ? "No enabled flows."
            : string.Join('\n', items.Select(f => $"flow_{f.Slug} — {f.Name}: {f.Description ?? "(no description)"}"));
        return await ToolResults.Structured(text, items);
    }

    private static async ValueTask<CallToolResult> RunFlowAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var flowRef = ToolArgs.RequiredString(ctx, "flow");
        var inputs = ToolArgs.OptionalObject(ctx, "inputs");

        var db = ctx.Services.GetRequiredService<Data.KnowledgeHubDbContext>();
        var flow = Guid.TryParse(flowRef, out var id)
            ? await db.AgentFlows.FirstOrDefaultAsync(f => f.Id == id && f.Enabled, ct)
            : await db.AgentFlows.FirstOrDefaultAsync(f => f.Slug == flowRef && f.Enabled, ct);
        if (flow is null)
            return await ToolResults.Error($"flow '{flowRef}' not found or disabled");

        return await InvokeFlowAsync(ctx, flow, inputs?.Deserialize<JsonObject>(), ct);
    }

    private static ValueTask<CallToolResult> InvokeFlowAsync(
        ToolCallContext ctx, Domain.Entities.AgentFlow flow, CancellationToken ct) =>
        InvokeFlowAsync(ctx, flow, InputsFromArgs(ctx), ct);

    private static async ValueTask<CallToolResult> InvokeFlowAsync(
        ToolCallContext ctx, Domain.Entities.AgentFlow flow, JsonObject? inputs, CancellationToken ct)
    {
        var service = ctx.Services.GetRequiredService<FlowService>();
        var apiKeyId = CallerIdentity.TryGetApiKeyId(ctx);
        try
        {
            var result = await service.RunAsync(flow, inputs, ctx.Services, apiKeyId, sink: null, ct);
            var text = result.Status == "done"
                ? $"flow '{flow.Slug}' completed ({result.Steps.Count} steps, {result.DurationMs}ms)\n" +
                  (result.Output?.ToJsonString() ?? "")
                : $"flow '{flow.Slug}' failed: {result.Error}";
            return result.Status == "done"
                ? await ToolResults.Structured(text, new
                {
                    runId = result.RunId,
                    status = result.Status,
                    output = result.Output,
                    steps = result.Steps.Select(s => new
                    {
                        s.StepId,
                        s.StepType,
                        s.Status,
                        s.Error,
                        s.DurationMs,
                    }),
                    result.DurationMs,
                })
                : await ToolResults.Error(text);
        }
        catch (FlowAbortException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (Exception ex)
        {
            return await ToolResults.Error($"flow '{flow.Slug}' run failed: {ex.Message}");
        }
    }

    /// <summary>The flow_&lt;slug&gt; call-args object IS the inputs object —
    /// each declared input arrives as a top-level arg.</summary>
    private static JsonObject? InputsFromArgs(ToolCallContext ctx)
    {
        if (ctx.Arguments is not { Count: > 0 })
            return null;
        var obj = new JsonObject();
        foreach (var (key, value) in ctx.Arguments)
            obj[key] = JsonNode.Parse(value.GetRawText());
        return obj;
    }
}
