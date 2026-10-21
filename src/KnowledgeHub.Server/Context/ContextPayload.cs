using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace KnowledgeHub.Server.Context;

/// <summary>
/// Typed context item persisted inside a snapshot payload
/// (SPEC-20261004-context-snapshots RF-003). Unknown JSON fields are rejected
/// during parse — payloads are never arbitrary blobs.
/// </summary>
public sealed class ContextItem
{
    public required string Kind { get; set; }
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Path { get; set; }
    /// <summary>Originating tool name for <c>tool_output</c> items — feeds the
    /// elision stub marker <c>Tool: &lt;name|unknown&gt;</c>.</summary>
    public string? Tool { get; set; }
    public required string Content { get; set; }
    public double? Score { get; set; }
}

public static class ContextKinds
{
    public const string Decision = "decision";
    public const string Note = "note";
    public const string Passage = "passage";
    public const string ToolOutput = "tool_output";
    public const string Summary = "summary";

    /// <summary>Kinds the compactor may elide or drop — the protected set
    /// (decision/note/summary) is never elided or removed.</summary>
    public static bool IsDisposable(string kind) =>
        kind is ToolOutput or Passage;
}

/// <summary>Parsed + validated <c>{items:[…]}</c> payload.</summary>
public sealed class ContextPayload
{
    public required List<ContextItem> Items { get; set; }

    private const int MaxContentChars = 16_000;
    private const int MaxTitleChars = 500;
    private const int MaxPathChars = 1000;
    private const int MaxToolChars = 200;
    private const int MaxIdChars = 200;

    private static readonly string[] SecretMarkers =
        ["aft_", "sk-", "ctx7sk-", "ghp_", "gho_", "github_pat_", "xoxb-", "xoxp-", "xoxa-", "AIza", "-----BEGIN"];

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerOptions.Web);

    /// <summary>Serializes canonically (stable property order) — the output is
    /// the input of the compact-cache hash (RF-007).</summary>
    public static string Serialize(IReadOnlyList<ContextItem> items) =>
        JsonSerializer.Serialize(new ContextPayload { Items = [.. items] }, ParseOptions);

    public static ContextPayload Deserialize(string json) =>
        JsonSerializer.Deserialize<ContextPayload>(json, ParseOptions) ?? new ContextPayload { Items = [] };

    /// <summary>Parses the <c>items</c> argument strictly: unknown fields,
    /// wrong kinds, oversized strings, control characters and secret markers
    /// all fail with <see cref="McpProtocolException"/> (the caller maps the
    /// code text into <c>invalid_payload</c>/<c>secret_detected</c>).</summary>
    public static List<ContextItem> ParseItems(JsonElement? itemsElement, int maxItems)
    {
        if (itemsElement is not { } el || el.ValueKind != JsonValueKind.Array)
            throw new McpProtocolException("invalid_payload: 'items' must be an array", McpErrorCode.InvalidParams);

        var items = new List<ContextItem>(el.GetArrayLength());
        var index = 0;
        foreach (var itemEl in el.EnumerateArray())
        {
            items.Add(ParseItem(itemEl, index));
            index++;
        }
        if (items.Count > maxItems)
            throw new McpProtocolException(
                $"payload_too_large: items count {items.Count} exceeds limit {maxItems}",
                McpErrorCode.InvalidParams);
        return items;
    }

    private static ContextItem ParseItem(JsonElement el, int index)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw Invalid(index, "item must be an object");

        ContextItem item = new() { Kind = "", Content = "" };
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "kind": item.Kind = RequiredString(prop, index, "kind"); break;
                case "id": item.Id = OptionalBoundedString(prop, index, MaxIdChars); break;
                case "title": item.Title = OptionalBoundedString(prop, index, MaxTitleChars); break;
                case "path": item.Path = OptionalBoundedString(prop, index, MaxPathChars); break;
                case "tool": item.Tool = OptionalBoundedString(prop, index, MaxToolChars); break;
                case "content": item.Content = RequiredString(prop, index, "content"); break;
                case "score":
                    if (prop.Value.ValueKind != JsonValueKind.Number)
                        throw Invalid(index, "score must be a number");
                    item.Score = prop.Value.GetDouble();
                    break;
                default:
                    throw Invalid(index, $"unknown field '{prop.Name}'");
            }
        }

        if (item.Kind is not (ContextKinds.Decision or ContextKinds.Note or ContextKinds.Passage
            or ContextKinds.ToolOutput or ContextKinds.Summary))
            throw Invalid(index, $"unknown kind '{item.Kind}'");
        if (item.Content.Length > MaxContentChars)
            throw Invalid(index, $"content exceeds {MaxContentChars} chars");

        foreach (var field in new[] { item.Content, item.Title, item.Path, item.Tool, item.Id })
        {
            if (field is null)
                continue;
            if (field.Any(ch => char.IsControl(ch) && ch is not '\n' and not '\t'))
                throw Invalid(index, "control characters are not allowed");
            if (ContainsSecret(field))
                throw new McpProtocolException(
                    $"secret_detected: item #{index} contains a credential pattern — redact before saving",
                    McpErrorCode.InvalidParams);
        }
        return item;
    }

    /// <summary>Bounded free-text fields (label) get the same secret scan.</summary>
    public static void EnsureNoSecret(string? value, string fieldName)
    {
        if (value is not null && ContainsSecret(value))
            throw new McpProtocolException(
                $"secret_detected: '{fieldName}' contains a credential pattern",
                McpErrorCode.InvalidParams);
    }

    private static bool ContainsSecret(string value)
    {
        foreach (var marker in SecretMarkers)
            if (value.Contains(marker, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string RequiredString(JsonProperty prop, int index, string name) =>
        prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } s
            ? s
            : throw Invalid(index, $"'{name}' must be a string");

    private static string? OptionalBoundedString(JsonProperty prop, int index, int max)
    {
        if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (prop.Value.ValueKind != JsonValueKind.String)
            throw Invalid(index, $"'{prop.Name}' must be a string");
        var s = prop.Value.GetString()!;
        if (s.Length > max)
            throw Invalid(index, $"'{prop.Name}' exceeds {max} chars");
        return s;
    }

    private static McpProtocolException Invalid(int index, string detail) =>
        new($"invalid_payload: item #{index} — {detail}", McpErrorCode.InvalidParams);

    /// <summary>~4 chars per token — the repo-wide estimate convention.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    public static int EstimateTokens(ContextItem item) =>
        EstimateTokens(item.Content)
        + EstimateTokens(item.Title ?? "")
        + EstimateTokens(item.Path ?? "")
        + EstimateTokens(item.Tool ?? "");
}
