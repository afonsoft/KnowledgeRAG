using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260927-ragflow-vision-layout-chunking RF-001/RF-002/RF-003:
/// detects markdown (`|---|`) and HTML (`&lt;table&gt;`) table blocks and splits
/// oversized ones by rows, repeating the header on every continuation chunk.
/// Detection is line-prefix/span based — no regex over table bodies (ReDoS-safe).
/// </summary>
public static class TableChunkSplitter
{
    /// <summary>True when the trimmed line opens a markdown or HTML table row.</summary>
    public static bool StartsTable(string line)
    {
        var t = line.AsSpan().TrimStart();
        return t.StartsWith('|') || t.StartsWith("<table", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fast pre-scan used by the selector: does the text contain a table?</summary>
    public static bool ContainsTable(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.AsSpan().TrimStart();
            if (t.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
                return true;
            // markdown separator row `|---|` (or `| --- |`) is the reliable signal
            if (t.StartsWith('|') && IsSeparatorRow(t))
                return true;
        }
        return false;
    }

    /// <summary>A `|` row consisting only of pipes, dashes, colons and spaces.</summary>
    private static bool IsSeparatorRow(ReadOnlySpan<char> t)
    {
        var seenDash = false;
        foreach (var ch in t)
        {
            if (ch == '-') seenDash = true;
            else if (ch is not ('|' or ':' or ' ' or '\t' or '\r')) return false;
        }
        return seenDash;
    }

    /// <summary>
    /// Reads the table block starting at <paramref name="start"/> and returns the
    /// exclusive end line index. Markdown tables = consecutive `|` lines;
    /// HTML tables = up to and including the line holding `&lt;/table&gt;`.
    /// </summary>
    public static int ReadTableEnd(string[] lines, int start)
    {
        var first = lines[start].AsSpan().TrimStart();
        if (first.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
        {
            var i = start;
            while (i < lines.Length)
            {
                if (lines[i].Contains("</table>", StringComparison.OrdinalIgnoreCase))
                    return i + 1;
                i++;
            }
            return lines.Length; // unterminated — treat rest as table
        }

        var end = start;
        while (end < lines.Length && lines[end].AsSpan().TrimStart().StartsWith('|'))
            end++;
        return end;
    }

    /// <summary>Extracts markdown header cells from the header row.</summary>
    public static List<string> HeaderCells(string headerLine)
    {
        var t = headerLine.Trim().Trim('|');
        return t.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
    }

    /// <summary>
    /// Splits a table block into chunks. ≤ <paramref name="maxRows"/> data rows →
    /// a single atomic piece. Longer tables split into slices of at most
    /// <paramref name="maxRows"/> data rows each, every slice repeating the
    /// header row + separator; continuations get a
    /// <c>[Continuação da Tabela - Linhas X a Y]</c> marker (SPEC RF-002).
    /// </summary>
    public static IReadOnlyList<ChunkPiece> Split(
        IReadOnlyList<string> tableLines, int maxRows, bool preserveHeaders,
        string? sectionPath)
    {
        var lines = tableLines.Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
            return [];

        var markdown = lines[0].TrimStart().StartsWith('|');
        var headerIdx = 0;
        var dataStart = 1;
        if (markdown && lines.Count > 1 && IsSeparatorRow(lines[1].TrimStart()))
            dataStart = 2; // skip the |---| separator — re-emitted per slice

        var dataRows = lines.Skip(dataStart).ToList();
        var headers = markdown ? HeaderCells(lines[headerIdx]) : [];

        string BuildSlice(List<string> rows, int firstRow, int lastRow, bool continuation)
        {
            var sb = new System.Text.StringBuilder();
            if (continuation)
                sb.Append("[Continuação da Tabela - Linhas ")
                    .Append(firstRow).Append(" a ").Append(lastRow).Append("]\n");
            if (preserveHeaders)
            {
                sb.Append(lines[headerIdx]).Append('\n');
                if (markdown && lines.Count > 1 && dataStart == 2)
                    sb.Append(lines[1]).Append('\n');
            }
            foreach (var r in rows)
                sb.Append(r).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        var meta = Metadata(headers, dataRows.Count, sectionPath);
        var pieces = new List<ChunkPiece>();
        if (dataRows.Count <= Math.Max(1, maxRows))
        {
            pieces.Add(new ChunkPiece(
                BuildSlice(dataRows, 1, dataRows.Count, continuation: false),
                SectionPath: sectionPath, MetadataJson: meta));
            return pieces;
        }

        for (var i = 0; i < dataRows.Count; i += Math.Max(1, maxRows))
        {
            var slice = dataRows.Skip(i).Take(Math.Max(1, maxRows)).ToList();
            pieces.Add(new ChunkPiece(
                BuildSlice(slice, i + 1, i + slice.Count, continuation: i > 0),
                SectionPath: sectionPath, MetadataJson: meta));
        }
        return pieces;
    }

    private static string Metadata(List<string> headers, int rowCount, string? sectionPath) =>
        JsonSerializer.Serialize(new
        {
            is_table = true,
            table_headers = headers,
            table_rows_count = rowCount,
            table_columns = headers.Count,
            parent_section_title = sectionPath
        });
}
