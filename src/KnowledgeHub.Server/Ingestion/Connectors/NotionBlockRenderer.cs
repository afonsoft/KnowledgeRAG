using System.Text;
using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>One Notion block with its materialized children (tree built by the connector).</summary>
public sealed record NotionBlock(JsonElement Element, IReadOnlyList<NotionBlock> Children);

/// <summary>
/// Flattens a Notion block tree into markdown-ish text
/// (SPEC-20260919-notion-connector RF-004) and serializes database row
/// properties (RF-005). Pure/sync — the connector owns fetching, bounds and
/// child-page/database discovery.
/// </summary>
public static class NotionBlockRenderer
{
    private const string TitleProp = "title";

    /// <summary>Renders a block tree. Nested children are indented two spaces per level.</summary>
    public static string Render(IReadOnlyList<NotionBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
            RenderBlock(block, 0, sb);
        return sb.ToString().TrimEnd('\n');
    }

    private static void RenderBlock(NotionBlock block, int depth, StringBuilder sb)
    {
        var element = block.Element;
        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (type is null)
            return;

        var payload = element.TryGetProperty(type, out var p) ? p : default;
        RenderPayload(type, payload, new string(' ', depth * 2), sb);

        foreach (var child in block.Children)
            RenderBlock(child, depth + 1, sb);
    }

    /// <summary>Markdown prefix for text-ish blocks; null for unknown types,
    /// which are skipped silently (SPEC edge cases). "toggle" renders its
    /// summary line — children are rendered below by the caller.</summary>
    private static readonly Dictionary<string, string> TextPrefixes = new()
    {
        ["heading_1"] = "# ",
        ["heading_2"] = "## ",
        ["heading_3"] = "### ",
        ["paragraph"] = "",
        ["bulleted_list_item"] = "- ",
        ["numbered_list_item"] = "1. ",
        ["quote"] = "> ",
        ["callout"] = "> ",
        ["toggle"] = "",
    };

    private static void RenderPayload(string type, JsonElement payload, string indent, StringBuilder sb)
    {
        switch (type)
        {
            case "table":
                // table_width lives on the payload; rows are child blocks.
                break;
            case "table_row":
                RenderTableRow(payload, indent, sb);
                break;
            case "child_page":
                Line(sb, indent, $"[página: {StringProp(payload, TitleProp)}]");
                break;
            case "child_database":
                Line(sb, indent, $"[database: {StringProp(payload, TitleProp)}]");
                break;
            case "image":
            case "video":
                Line(sb, indent, $"[{MediaLabel(type)}: {MediaUrl(payload)}]");
                break;
            case "file":
            case "pdf":
                Line(sb, indent, $"[arquivo: {MediaUrl(payload)}]");
                break;
            case "embed":
            case "bookmark":
                Line(sb, indent, $"[{type}: {StringProp(payload, "url")}]");
                break;
            default:
                RenderTextElement(type, payload, indent, sb);
                break;
        }
    }

    private static void RenderTextElement(string type, JsonElement payload, string indent, StringBuilder sb)
    {
        switch (type)
        {
            case "code":
                RenderCode(payload, indent, sb);
                break;
            case "divider":
                Line(sb, indent, "---");
                break;
            default:
                var prefix = type == "to_do"
                    ? ToDoPrefix(payload)
                    : TextPrefixes.GetValueOrDefault(type);
                if (prefix is not null)
                    Line(sb, indent, prefix + RichText(payload));
                break;
        }
    }

    private static string ToDoPrefix(JsonElement payload) =>
        IsChecked(payload) ? "- [x] " : "- [ ] ";

    private static bool IsChecked(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.True;

    private static void RenderCode(JsonElement payload, string indent, StringBuilder sb)
    {
        var lang = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() : "";
        Line(sb, indent, "```" + lang);
        Line(sb, indent, RichText(payload));
        Line(sb, indent, "```");
    }

    private static void RenderTableRow(JsonElement payload, string indent, StringBuilder sb)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("cells", out var cells)
            && cells.ValueKind == JsonValueKind.Array)
        {
            var row = string.Join(" | ", cells.EnumerateArray()
                .Select(cell => cell.ValueKind == JsonValueKind.Array
                    ? string.Concat(cell.EnumerateArray().Select(PlainText))
                    : ""));
            Line(sb, indent, row);
        }
    }

    private static void Line(StringBuilder sb, string indent, string text) =>
        sb.Append(indent).Append(text).Append('\n');

    private static string RichText(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("rich_text", out var rt)
        && rt.ValueKind == JsonValueKind.Array
            ? string.Concat(rt.EnumerateArray().Select(PlainText))
            : "";

    private static string PlainText(JsonElement fragment) =>
        fragment.ValueKind == JsonValueKind.Object
        && fragment.TryGetProperty("plain_text", out var pt) && pt.ValueKind == JsonValueKind.String
            ? pt.GetString() ?? ""
            : "";

    private static string? StringProp(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string MediaLabel(string type) => type == "image" ? "imagem" : type;

    /// <summary>Media payloads nest the URL under <c>external</c>/<c>file</c>.</summary>
    private static string? MediaUrl(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var kind in new[] { "external", "file" })
        {
            if (payload.TryGetProperty(kind, out var k) && k.ValueKind == JsonValueKind.Object
                && k.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
        }
        return null;
    }

    /// <summary>Page/row title: first property of type <c>title</c>, concatenated
    /// plain_text; falls back to the page id (RF-003).</summary>
    public static string ExtractPageTitle(JsonElement page)
    {
        if (page.ValueKind == JsonValueKind.Object
            && page.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
        {
            var text = props.EnumerateObject()
                .Where(prop => prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() == TitleProp
                    && prop.Value.TryGetProperty(TitleProp, out var title)
                    && title.ValueKind == JsonValueKind.Array)
                .Select(prop => string.Concat(
                    prop.Value.GetProperty(TitleProp).EnumerateArray().Select(PlainText)))
                .FirstOrDefault(t => t.Length > 0);
            if (text is not null)
                return text;
        }
        return page.ValueKind == JsonValueKind.Object
            && page.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? "(untitled)"
                : "(untitled)";
    }

    /// <summary>Serializes a row's <c>properties</c> object into <c>Name: value</c>
    /// lines (RF-005); unsupported types degrade to <c>(unsupported)</c>.</summary>
    public static IReadOnlyList<string> SerializeProperties(JsonElement properties)
    {
        var lines = new List<string>();
        if (properties.ValueKind != JsonValueKind.Object)
            return lines;

        foreach (var prop in properties.EnumerateObject())
            lines.Add($"{prop.Name}: {PropertyValue(prop.Value)}");
        return lines;
    }

    private static string? Text(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string JoinText(JsonElement array, Func<JsonElement, string?> pick) =>
        array.ValueKind == JsonValueKind.Array
            ? string.Join(", ", array.EnumerateArray().Select(pick).Where(v => v is not null))
            : "";

    private static string PropertyValue(JsonElement prop)
    {
        if (prop.ValueKind != JsonValueKind.Object
            || !prop.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            return "(unsupported)";

        var type = t.GetString()!;
        var payload = prop.TryGetProperty(type, out var p) ? p : default;
        return ScalarPropertyValue(type, payload) ?? StructuredPropertyValue(type, payload);
    }

    /// <summary>Scalar/label property types; null when the type is structured.</summary>
    private static string? ScalarPropertyValue(string type, JsonElement payload) => type switch
    {
        TitleProp or "rich_text" => RichTextConcat(payload),
        "number" => payload.ValueKind == JsonValueKind.Number ? payload.GetRawText() : "",
        "select" or "status" => NamedProp(payload, ""),
        "date" => DateRangeProp(payload),
        "checkbox" => payload.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? payload.GetBoolean().ToString() : "",
        "url" or "email" or "phone_number" or "created_time" or "last_edited_time" =>
            Text(payload) ?? "",
        "created_by" or "last_edited_by" => NamedProp(payload, "(user)"),
        _ => null
    };

    private static string RichTextConcat(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Array
            ? string.Concat(payload.EnumerateArray().Select(PlainText))
            : "";

    private static string NamedProp(JsonElement payload, string fallback) =>
        payload.ValueKind == JsonValueKind.Object ? StringProp(payload, "name") ?? fallback : "";

    private static string DateRangeProp(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
            ? string.Join(" → ", new[] { StringProp(payload, "start"), StringProp(payload, "end") }
                .Where(v => v is not null))
            : "";

    /// <summary>Array/reference property types; degrades to <c>(unsupported)</c>.</summary>
    private static string StructuredPropertyValue(string type, JsonElement payload) => type switch
    {
        "multi_select" or "people" or "files" => JoinText(payload, e => StringProp(e, "name")),
        "relation" => JoinText(payload, e => StringProp(e, "id")),
        "formula" => FormulaValue(payload),
        _ => "(unsupported)"
    };

    private static string FormulaValue(JsonElement formula)
    {
        if (formula.ValueKind != JsonValueKind.Object
            || !formula.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            return "";
        var type = t.GetString()!;
        if (!formula.TryGetProperty(type, out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => v.GetBoolean().ToString(),
            JsonValueKind.Object => StringProp(v, "start") ?? "",
            _ => ""
        };
    }
}
