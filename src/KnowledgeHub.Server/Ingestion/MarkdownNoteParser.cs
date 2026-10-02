using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>
/// Parses Obsidian-style Markdown notes: YAML frontmatter, tags
/// (frontmatter <c>tags</c>/<c>tag</c> + inline <c>#tag</c>), <c>[[WikiLink]]</c>
/// targets, and title (first <c># H1</c>, else filename). Malformed YAML is
/// tolerated — the body is still indexed (SPEC-03 RF-001).
/// </summary>
public static partial class MarkdownNoteParser
{
    private const int MaxFrontmatterBytes = 64 * 1024;

    public static ParsedNote Parse(string raw, string fileName)
    {
        var (frontmatter, body) = SplitFrontmatter(raw);
        var title = ExtractTitle(body) ?? Path.GetFileNameWithoutExtension(fileName);
        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();

        CollectFrontmatterTags(frontmatter, tags);
        foreach (Match m in InlineTagRegex().Matches(StripWikiAndCode(body)))
            tags.Add(m.Groups[1].Value);
        foreach (Match m in WikiLinkRegex().Matches(body))
        {
            var target = m.Groups[1].Value.Split('|')[0].Split('#')[0].Trim();
            if (target.Length > 0)
                links.Add(target);
        }

        return new ParsedNote
        {
            Title = title,
            Body = body.Trim(),
            Frontmatter = frontmatter,
            Tags = [.. tags],
            WikiLinks = links
        };
    }

    private static (IReadOnlyDictionary<string, object?> Frontmatter, string Body) SplitFrontmatter(string raw)
    {
        var empty = new Dictionary<string, object?>();
        if (!raw.StartsWith("---", StringComparison.Ordinal))
            return (empty, raw);

        // Frontmatter must start at line 1 and be a `---` fenced block.
        var firstLineEnd = raw.IndexOf('\n');
        if (firstLineEnd < 0 || raw.AsSpan(0, firstLineEnd).Trim() is not "---")
            return (empty, raw);

        var closeIdx = raw.IndexOf("\n---", firstLineEnd + 1, StringComparison.Ordinal);
        if (closeIdx < 0 || closeIdx > MaxFrontmatterBytes)
            return (empty, raw);

        var afterClose = raw.IndexOf('\n', closeIdx + 4);
        var yamlText = raw[(firstLineEnd + 1)..closeIdx];
        var body = afterClose < 0 ? "" : raw[(afterClose + 1)..];

        try
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(yamlText);
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode map)
                return (empty, body);

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in map.Children)
            {
                var key = entry.Key.ToString();
                dict[key] = entry.Value switch
                {
                    YamlScalarNode scalar => scalar.Value,
                    YamlSequenceNode seq => seq.Children.Select(c => c.ToString()).ToList(),
                    var other => other.ToString()
                };
            }
            return (dict, body);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return (empty, body); // malformed YAML → index body anyway
        }
    }

    private static void CollectFrontmatterTags(IReadOnlyDictionary<string, object?> frontmatter, ISet<string> tags)
    {
        foreach (var key in new[] { "tags", "tag" })
        {
            if (!frontmatter.TryGetValue(key, out var value) || value is null)
                continue;
            switch (value)
            {
                case string single:
                    foreach (var t in single.Split(' ', ',', StringSplitOptions.RemoveEmptyEntries))
                        tags.Add(t.TrimStart('#'));
                    break;
                case List<string> many:
                    foreach (var t in many)
                        tags.Add(t.TrimStart('#'));
                    break;
            }
        }
    }

    private static string? ExtractTitle(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                return null; // first non-empty line isn't an H1 → fallback to filename
        }
        return null;
    }

    // Avoid treating '#'-inside-wikilinks or code fences as tags.
    private static string StripWikiAndCode(string body) =>
        CodeFenceRegex().Replace(WikiLinkRegex().Replace(body, " "), " ");

    [GeneratedRegex(@"\[\[([^\]]+)\]\]")] private static partial Regex WikiLinkRegex();
    [GeneratedRegex(@"#([A-Za-z][\w/-]*)")] private static partial Regex InlineTagRegex();
    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)] private static partial Regex CodeFenceRegex();
}
