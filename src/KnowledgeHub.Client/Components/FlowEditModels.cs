using System.Text.Json;
using System.Text.Json.Nodes;
using BootstrapBlazor.Components;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Components;

/// <summary>Which editor surface the flows page shows for a flow definition.</summary>
public enum FlowEditorView
{
    List,
    Canvas,
    Json,
}

/// <summary>Editable view-model for a declared flow input.</summary>
public sealed class FlowInputModel
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? DefaultJson { get; set; }
    public string? Description { get; set; }
}

/// <summary>Editable view-model for one flow step. <see cref="ConfigJson"/> and
/// <see cref="StepsJson"/> stay as raw JSON text — the canvas edits structure
/// (order/add/remove/select), the drawer edits payloads.</summary>
public sealed class FlowStepModel
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "tool";
    public string? Name { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string StepsJson { get; set; } = "";
}

/// <summary>Editable view-model for a whole flow (list editor, canvas and raw
/// JSON all write through this object).</summary>
public sealed class FlowEditModel
{
    public Guid? Id;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled = true;
    public List<FlowInputModel> Inputs { get; } = new();
    public List<FlowStepModel> Steps { get; } = new();
    public string DefinitionJson { get; set; } = "{}";
    /// <summary>Active editor surface. When <see cref="FlowEditorView.Json"/>,
    /// <see cref="DefinitionJson"/> is authoritative over Inputs/Steps.</summary>
    public FlowEditorView View = FlowEditorView.List;

    public static readonly List<SelectedItem> InputTypes =
    [
        new("string", "string"), new("number", "number"), new("boolean", "boolean"),
        new("object", "object"), new("array", "array"),
    ];

    public static readonly List<SelectedItem> StepTypes =
    [
        new("tool", "tool — catálogo MCP"),
        new("knowledge", "knowledge — search/ask"),
        new("llm", "llm — prompt"),
        new("http", "http — chamada externa"),
        new("condition", "condition — branches"),
        new("foreach", "foreach — loop"),
        new("transform", "transform — reshape"),
        new("output", "output — resultado"),
        new("fail", "fail — abortar"),
    ];

    public static FlowEditModel Empty() => new()
    {
        DefinitionJson = """
            {"inputs":[],"steps":[{"id":"step1","type":"tool","config":{"tool":"search_knowledge","args":{"query":"{{vars.q}}"}}}]}
            """,
    };

    public static FlowEditModel From(FlowDetailDto detail)
    {
        var model = new FlowEditModel
        {
            Id = detail.Flow.Id,
            Name = detail.Flow.Name,
            Description = detail.Flow.Description,
            Enabled = detail.Flow.Enabled,
            DefinitionJson = JsonSerializer.Serialize(detail.Definition, SharedJson.Options),
        };
        foreach (var i in detail.Definition.Inputs)
        {
            model.Inputs.Add(new FlowInputModel
            {
                Name = i.Name,
                Type = i.Type,
                Required = i.Required,
                Description = i.Description,
                DefaultJson = i.Default?.ToJsonString(),
            });
        }
        foreach (var s in detail.Definition.Steps)
        {
            model.Steps.Add(new FlowStepModel
            {
                Id = s.Id,
                Type = s.Type,
                Name = s.Name,
                ConfigJson = s.Config?.ToJsonString() ?? "{}",
                StepsJson = s.Steps is { Count: > 0 }
                    ? JsonSerializer.Serialize(s.Steps, SharedJson.Options)
                    : "",
            });
        }
        return model;
    }
}

/// <summary>Display helpers shared by the canvas nodes and nested-step chips —
/// icons per step type and lane extraction for condition/foreach bodies.</summary>
public static class FlowVisuals
{
    /// <summary>One rendered lane: a label (branch condition or loop) plus the
    /// nested steps that run inside it.</summary>
    public sealed record FlowLane(string Label, IReadOnlyList<FlowStepDto> Steps);

    public static string TypeIcon(string type) => type switch
    {
        "tool" => "fa-screwdriver-wrench",
        "knowledge" => "fa-magnifying-glass",
        "llm" => "fa-brain",
        "http" => "fa-globe",
        "condition" => "fa-code-branch",
        "foreach" => "fa-repeat",
        "transform" => "fa-wand-magic-sparkles",
        "output" => "fa-flag-checkered",
        "fail" => "fa-circle-xmark",
        _ => "fa-puzzle-piece",
    };

    /// <summary>Lanes from the editable model (foreach → StepsJson, condition →
    /// ConfigJson branches/else). Parse failures yield no lanes — the raw text
    /// stays editable in the drawer.</summary>
    public static List<FlowLane> DisplayLanes(FlowStepModel step)
    {
        if (step.Type == "foreach")
            return LaneList("loop", step.StepsJson);
        if (step.Type != "condition")
            return [];

        JsonObject? config;
        try { config = JsonNode.Parse(step.ConfigJson) as JsonObject; }
        catch (JsonException) { return []; }
        return ConditionLanes(config);
    }

    /// <summary>Lanes from a parsed DTO (nested chips recursion).</summary>
    public static List<FlowLane> DisplayLanes(FlowStepDto step)
    {
        if (step.Type == "foreach")
            return [new FlowLane("loop", step.Steps ?? [])];
        if (step.Type != "condition")
            return [];
        return ConditionLanes(step.Config);
    }

    private static List<FlowLane> LaneList(string label, string? stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
            return [new FlowLane(label, [])];
        try
        {
            var steps = JsonSerializer.Deserialize<List<FlowStepDto>>(stepsJson, SharedJson.Options);
            return [new FlowLane(label, steps ?? [])];
        }
        catch (JsonException) { return [new FlowLane(label, [])]; }
    }

    private static List<FlowLane> ConditionLanes(JsonObject? config)
    {
        var lanes = new List<FlowLane>();
        if (config?["branches"] is JsonArray branches)
        {
            foreach (var branch in branches)
            {
                if (branch is not JsonObject b)
                    continue;
                var label = b["when"] is JsonObject when ? WhenText(when) : "sempre";
                lanes.Add(new FlowLane(label, ParseStepsNode(b["steps"])));
            }
        }
        if (config?["else"] is JsonArray elseSteps)
            lanes.Add(new FlowLane("else", ParseStepsNode(elseSteps)));
        return lanes;
    }

    private static List<FlowStepDto> ParseStepsNode(JsonNode? node)
    {
        if (node is null)
            return [];
        try
        {
            return node.Deserialize<List<FlowStepDto>>(SharedJson.Options) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static string WhenText(JsonObject when)
    {
        var left = NodeText(when["left"]) ?? "?";
        var op = NodeText(when["op"]) ?? "?";
        var right = NodeText(when["right"]);
        return right is null ? $"{left} {op}" : $"{left} {op} {right}";
    }

    private static string? NodeText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    /// <summary>Short summary badge for the node body (tool name, http method+url…).</summary>
    public static string? Summary(FlowStepModel step)
    {
        if (string.IsNullOrWhiteSpace(step.ConfigJson))
            return null;
        JsonObject? config;
        try { config = JsonNode.Parse(step.ConfigJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (config is null)
            return null;
        return step.Type switch
        {
            "tool" or "knowledge" => NodeText(config["tool"]),
            "http" => $"{NodeText(config["method"]) ?? "GET"} {NodeText(config["url"]) ?? ""}".Trim(),
            "llm" => NodeText(config["prompt"]) is { } p ? (p.Length > 60 ? p[..60] + "…" : p) : null,
            "transform" => "reshape",
            "output" => config["value"]?.ToJsonString() is { } v ? (v.Length > 60 ? v[..60] + "…" : v) : null,
            "fail" => NodeText(config["message"]),
            "foreach" => $"em {config["each"]?.ToJsonString() ?? "?"}",
            _ => null,
        };
    }
}
