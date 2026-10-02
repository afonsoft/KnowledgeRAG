using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260923-code-aware-chunking RF-003: config chunker — JSON splits on
/// top-level members (<c>$.path:</c> preamble), YAML on top-level keys
/// (<c>key:</c>), XML on top-level elements (<c>&lt;root&gt;/&lt;child&gt;</c>).
/// Malformed input falls back to prose (markdown) chunking — never throws.
/// </summary>
public sealed partial class ConfigTextChunker : ITextChunker
{
    public static readonly ConfigTextChunker Instance = new();

    private const int CharsPerToken = 4;

    public ChunkKind Kind => ChunkKind.Config;

    [GeneratedRegex(@"^[\w.\-""']+\s*:")]
    private static partial Regex YamlKeyRegex();

    public IReadOnlyList<ChunkPiece> Chunk(string text, int maxTokens, int overlapTokens)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var maxChars = maxTokens * CharsPerToken;
        var nodes = DetectNodes(text);

        if (nodes is null || nodes.Count == 0)
            // Malformed or empty-structure input → prose fallback (never throw).
            return MarkdownTextChunker.Prose.Chunk(text, maxTokens, overlapTokens);

        var pieces = Pack(nodes, maxChars);
        var result = new List<ChunkPiece>();
        foreach (var (preamble, body) in pieces)
        {
            if (body.Length <= maxChars)
            {
                result.Add(new ChunkPiece(body, preamble));
                continue;
            }
            // Oversized node → line-window split keeping the path preamble.
            result.AddRange(SplitOversized(preamble, body, maxChars));
        }
        return result;
    }

    /// <summary>Picks the parser by leading character — JSON for
    /// <c>{</c>/<c>[</c>, XML for <c>&lt;</c>, YAML otherwise; a failed JSON
    /// parse falls through to YAML (never throws).</summary>
    private static List<(string? Path, string Body)>? DetectNodes(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var nodes = JsonNodes(text);
            if (nodes is not null)
                return nodes;
        }
        return trimmed.StartsWith('<') ? XmlNodes(text) : YamlNodes(text);
    }

    /// <summary>Line-window split of an oversized node, keeping the path preamble.</summary>
    private static List<ChunkPiece> SplitOversized(string? preamble, string body, int maxChars)
    {
        var current = new StringBuilder();
        var parts = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            if (current.Length + line.Length + 1 > maxChars && current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());

        var pieces = new List<ChunkPiece>();
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var header = $"{preamble} (part {i + 1}/{parts.Count})";
            var chunk = preamble is not null && !p.TrimStart().StartsWith(preamble, StringComparison.Ordinal)
                ? header + "\n" + p
                : p;
            pieces.Add(new ChunkPiece(chunk.Trim(), preamble));
        }
        return pieces;
    }

    /// <summary>JSON top-level members/elements with <c>$.path</c> preambles.</summary>
    private static List<(string? Path, string Body)>? JsonNodes(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var nodes = new List<(string?, string)>();
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                    nodes.Add(($"$.{prop.Name}", $"$.{prop.Name}:\n{Pretty(prop.Value)}"));
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in root.EnumerateArray())
                    nodes.Add(($"$[{i++}]", $"$[{i - 1}]:\n{Pretty(item)}"));
            }
            else
            {
                nodes.Add((null, root.GetRawText()));
            }
            return nodes;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>YAML top-level keys (indent-0 <c>key:</c> lines) as blocks.</summary>
    private static List<(string? Path, string Body)>? YamlNodes(string text)
    {
        var nodes = new List<(string?, string)>();
        var current = new StringBuilder();
        string? key = null;
        foreach (var line in text.Split('\n'))
        {
            var isTopKey = line.Length > 0 && !char.IsWhiteSpace(line[0]) && YamlKeyRegex().IsMatch(line);
            if (isTopKey && current.Length > 0)
            {
                nodes.Add((key, current.ToString().Trim()));
                current.Clear();
            }
            if (isTopKey)
                key = line.Split(':', 2)[0].Trim().Trim('"', '\'');
            current.AppendLine(line);
        }
        if (current.ToString().Trim().Length > 0)
            nodes.Add((key, current.ToString().Trim()));
        return nodes;
    }

    /// <summary>XML top-level child elements of the root as blocks.</summary>
    private static List<(string? Path, string Body)>? XmlNodes(string text)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(text);
            var root = doc.Root;
            if (root is null)
                return null;
            return root.Elements()
                .Select(el => ((string?)$"<{root.Name.LocalName}><{el.Name.LocalName}>",
                    $"<{root.Name.LocalName}>\n{el}"))
                .ToList();
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Greedy merge of nodes up to the char budget.</summary>
    private static List<(string? Path, string Body)> Pack(
        List<(string? Path, string Body)> nodes, int maxChars)
    {
        var packed = new List<(string?, string)>();
        var current = new StringBuilder();
        string? path = null;
        foreach (var (p, body) in nodes)
        {
            if (current.Length > 0 && current.Length + body.Length + 2 > maxChars)
            {
                packed.Add((path, current.ToString().Trim()));
                current.Clear();
                path = null;
            }
            path ??= p;
            current.Append(body).Append("\n\n");
        }
        if (current.ToString().Trim().Length > 0)
            packed.Add((path, current.ToString().Trim()));
        return packed;
    }

    private static string Pretty(JsonElement e) =>
        e.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true })
            : e.GetRawText();
}
