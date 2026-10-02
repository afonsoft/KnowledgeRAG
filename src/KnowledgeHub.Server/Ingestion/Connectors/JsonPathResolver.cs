using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// Minimal dot-path navigation over JSON (SPEC-20260927-restapi-sqldatabase-connectors
/// RF-002): segments separated by <c>.</c> walk object properties; a numeric
/// segment indexes an array. Deliberately no wildcards, filters or recursive
/// descent — that is the full JSONPath spec, explicitly out of scope.
/// </summary>
public static class JsonPathResolver
{
    /// <summary>Resolves <paramref name="path"/> against <paramref name="root"/>.
    /// Returns null when any segment is missing, indexes out of range or the
    /// walk hits a scalar. An empty path resolves to the root itself.</summary>
    public static JsonElement? Resolve(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return root;

        var current = root;
        foreach (var segment in path.Split('.').Select(s => s.Trim()))
        {
            if (segment.Length == 0)
                return null;

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out var property))
                    return null;
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= current.GetArrayLength())
                    return null;
                current = current[index];
            }
            else
            {
                return null;
            }
        }
        return current;
    }
}
