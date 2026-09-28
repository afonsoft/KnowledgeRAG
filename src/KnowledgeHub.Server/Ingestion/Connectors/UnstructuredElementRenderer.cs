using System.Text;
using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// SPEC-20260927-unstructured-document-parser-connector RF-002: renders the
/// Unstructured/Upstage element array into GFM Markdown. Titles become
/// <c>#</c> headings by depth, Table elements become GFM pipe tables (from
/// <c>metadata.text_as_html</c> when present), and page furniture
/// (Header/Footer) is suppressed so embeddings aren't polluted by repeated
/// page chrome.
/// </summary>
public static class UnstructuredElementRenderer
{
    public static string Render(JsonElement elements)
    {
        if (elements.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        var seenFurniture = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headingLevel = 1;

        foreach (var el in elements.EnumerateArray())
        {
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            var text = el.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String
                ? x.GetString()?.Trim() : null;

            switch (type)
            {
                case "Header":
                case "Footer":
                    // Repetitive page furniture — suppress duplicates entirely.
                    if (text is { Length: > 0 } && seenFurniture.Add($"{type}:{text}"))
                        sb.Append("\n\n<!-- ").Append(type).Append(": ").Append(text).Append(" -->");
                    continue;
                case "Title":
                    if (text is { Length: > 0 })
                        sb.Append("\n\n").Append('#', headingLevel).Append(' ').Append(text);
                    headingLevel = Math.Min(headingLevel + 1, 4);
                    continue;
                case "Table":
                    sb.Append("\n\n").Append(RenderTable(el, text));
                    continue;
                case "ListItem":
                    if (text is { Length: > 0 })
                        sb.Append("\n- ").Append(text);
                    continue;
                case "Image":
                case "FigureCaption":
                    if (text is { Length: > 0 })
                        sb.Append("\n\n> ").Append(text);
                    continue;
                default:
                    if (text is { Length: > 0 })
                        sb.Append("\n\n").Append(text);
                    continue;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Table element → GFM. Prefers <c>metadata.text_as_html</c>;
    /// falls back to fenced raw text when no HTML is available.</summary>
    private static string RenderTable(JsonElement el, string? text)
    {
        var html = el.TryGetProperty("metadata", out var m)
            && m.ValueKind == JsonValueKind.Object
            && m.TryGetProperty("text_as_html", out var h)
            && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;

        var table = html is { Length: > 0 } ? HtmlToGfm(html) : null;
        if (table is not null)
            return table;
        return text is { Length: > 0 } ? "```\n" + text + "\n```" : "";
    }

    /// <summary>Minimal deterministic &lt;table&gt;→GFM converter: extracts
    /// &lt;tr&gt; rows and &lt;th&gt;/&lt;td&gt; cells via span scans.</summary>
    internal static string? HtmlToGfm(string html)
    {
        var rows = new List<List<string>>();
        var cursor = 0;
        while ((cursor = html.IndexOf("<tr", cursor, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var rowEnd = html.IndexOf("</tr>", cursor, StringComparison.OrdinalIgnoreCase);
            var rowHtml = rowEnd < 0 ? html[cursor..] : html[cursor..rowEnd];
            cursor = rowEnd < 0 ? html.Length : rowEnd + 5;

            var cells = new List<string>();
            var i = 0;
            while (i < rowHtml.Length)
            {
                var th = rowHtml.IndexOf("<t", i, StringComparison.OrdinalIgnoreCase);
                if (th < 0) break;
                var close = rowHtml.IndexOf('>', th);
                if (close < 0) break;
                var cellEnd = rowHtml.IndexOf("</t", close, StringComparison.OrdinalIgnoreCase);
                var cell = cellEnd < 0 ? rowHtml[(close + 1)..] : rowHtml[(close + 1)..cellEnd];
                cells.Add(NormalizeCell(cell));
                i = cellEnd < 0 ? rowHtml.Length : cellEnd + 4;
            }
            if (cells.Count > 0)
                rows.Add(cells);
        }
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        var header = rows[0];
        sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n|");
        sb.Append(string.Join("|", header.Select(_ => "---"))).Append("|\n");
        foreach (var row in rows.Skip(1))
        {
            // pad short rows to the header width
            var padded = row.Concat(Enumerable.Repeat("", header.Count)).Take(header.Count);
            sb.Append("| ").Append(string.Join(" | ", padded)).Append(" |\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string NormalizeCell(string cell)
    {
        var sb = new StringBuilder(cell.Length);
        var inTag = false;
        foreach (var c in cell)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString().Trim().Replace("|", "\\|").Replace("\n", " ");
    }
}
