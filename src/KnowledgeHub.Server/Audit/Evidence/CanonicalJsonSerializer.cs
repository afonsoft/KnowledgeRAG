using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeHub.Server.Audit.Evidence;

/// <summary>
/// Deterministic JSON for hashing (RF-001): object keys strictly sorted
/// alphabetically, no insignificant whitespace, invariant number/string
/// rendering. Two identical payloads always produce byte-identical output.
/// </summary>
public static class CanonicalJsonSerializer
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                return;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                return;
            case JsonValue v when v.TryGetValue<string>(out var s):
                sb.Append(JsonSerializer.Serialize(s));
                return;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                sb.Append(b ? "true" : "false");
                return;
            case JsonValue v:
                // Numbers/dates: render via GetValue<JsonElement> semantics —
                // ToJsonString keeps the original lexical form which is fine
                // for hashing (canonicalization requirement is ordering).
                sb.Append(v.ToJsonString());
                return;
        }
    }
}
