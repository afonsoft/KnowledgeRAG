using System.Text.Json.Nodes;

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-006: the catalog lint requires every
/// tool schema to carry a top-level <c>examples</c> array. Schemas that already
/// annotate each property with its own <c>examples</c> get a synthesized
/// top-level example for free — required properties first, then optionals
/// until the cap.
/// </summary>
internal static class SchemaExamples
{
    private const int MaxExampleProps = 6;

    public static void Ensure(JsonObject schema)
    {
        if (schema["examples"] is JsonArray { Count: > 0 })
            return;
        if (schema["properties"] is not JsonObject props)
            return;

        var required = schema["required"] as JsonArray;
        var example = new JsonObject();
        var ordered = props
            .OrderBy(kv => required?.Any(r => r?.GetValue<string>() == kv.Key) == true ? 0 : 1);
        foreach (var (name, node) in ordered)
        {
            if (example.Count >= MaxExampleProps)
                break;
            if (node is not JsonObject prop)
                continue;
            if (prop["examples"] is JsonArray { Count: > 0 } ex)
                example[name] = ex[0]?.DeepClone();
            else if (prop["enum"] is JsonArray { Count: > 0 } en)
                example[name] = en[0]?.DeepClone();
            else
                example[name] = prop["type"]?.GetValue<string>() switch
                {
                    "integer" or "number" => 1,
                    "boolean" => true,
                    "array" => new JsonArray(),
                    _ => "…"
                };
        }

        if (example.Count > 0)
            schema["examples"] = new JsonArray(example);
    }
}
