using System.Text;
using HtmlAgilityPack;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// HTML → markdown-ish text (SPEC-20260914-webpage-docfile-connectors RF-001):
/// strips boilerplate, prefers article/main, preserves headings and list
/// markers. Optional include/exclude CSS-ish selectors (tag, .class, #id,
/// tag.class) narrow or prune the extraction scope.
/// </summary>
public static class HtmlTextExtractor
{
    private static readonly HashSet<string> DropTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "nav", "header", "footer", "aside", "form", "iframe", "svg" };

    public static string Extract(string html, string? includeSelector = null, string? excludeSelector = null)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var root = doc.DocumentNode;

        // Prefer semantic content containers when present.
        var scope = root.SelectSingleNode("//article") ?? root.SelectSingleNode("//main") ?? root;

        if (!string.IsNullOrWhiteSpace(includeSelector))
            scope = scope.SelectSingleNode(ToXPath(includeSelector)) ?? scope;

        var clone = scope.Clone();
        foreach (var node in clone.SelectNodes($".//{string.Join("|.//", DropTags)}")?.ToList() ?? [])
            node.Remove();
        foreach (var node in DropBySelector(clone, excludeSelector).ToList())
            node.Remove();

        var sb = new StringBuilder();
        Render(clone, sb);
        return CollapseWhitespace(sb.ToString());
    }

    public static string? ExtractTitle(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText
            ?? doc.DocumentNode.SelectSingleNode("//h1")?.InnerText;
        var decoded = title is null ? null : HtmlEntity.DeEntitize(title).Trim();
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    /// <summary>Extracts same-origin links for bounded crawling.</summary>
    public static IReadOnlyList<string> ExtractLinks(string html, Uri page)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return (doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            .Select(a => a.GetAttributeValue("href", ""))
            .Select(href => Uri.TryCreate(page, href, out var absolute) ? absolute : null)
            .Where(absolute => absolute is { Scheme: "http" or "https" }
                && absolute.Host.Equals(page.Host, StringComparison.OrdinalIgnoreCase))
            .Select(absolute => absolute!.GetLeftPart(UriPartial.Path))
            .Distinct()
            .ToList();
    }

    private static IEnumerable<HtmlNode> DropBySelector(HtmlNode scope, string? selector) =>
        string.IsNullOrWhiteSpace(selector)
            ? Enumerable.Empty<HtmlNode>()
            : scope.SelectNodes(ToXPath(selector)) ?? Enumerable.Empty<HtmlNode>();

    /// <summary>Minimal CSS→descendant-XPath: tag, .class, #id, tag.class.</summary>
    private static string ToXPath(string selector)
    {
        var s = selector.Trim();
        if (s.StartsWith('#'))
            return $".//*[@id='{s[1..]}']";
        if (s.StartsWith('.'))
            return $".//*[contains(concat(' ', normalize-space(@class), ' '), ' {s[1..]} ')]";
        var dot = s.IndexOf('.');
        if (dot > 0)
            return $".//{s[..dot]}[contains(concat(' ', normalize-space(@class), ' '), ' {s[(dot + 1)..]} ')]";
        return $".//{s}";
    }

    private static void Render(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
                sb.Append(HtmlEntity.DeEntitize(child.InnerText));
            else if (child.NodeType == HtmlNodeType.Element)
                RenderElement(child, sb);
        }
    }

    private static bool IsHeading(string tag) =>
        tag.StartsWith('h') && tag.Length == 2 && char.IsDigit(tag[1]);

    /// <summary>Renders one element: heading/list markers before its children,
    /// a paragraph break after block-level tags.</summary>
    private static void RenderElement(HtmlNode node, StringBuilder sb)
    {
        var tag = node.Name;
        var heading = IsHeading(tag);
        if (heading)
            sb.Append('\n').Append('#', tag[1] - '0').Append(' ');
        if (tag is "li")
            sb.Append("\n- ");
        Render(node, sb);
        if (heading || tag is "p" or "div" or "section" or "article" or "br" or "tr" or "table" or "ul" or "ol" or "blockquote")
            sb.Append("\n\n");
    }

    private static string CollapseWhitespace(string text)
    {
        var lines = text.Split('\n')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToList();
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (++blank > 1) continue;
            }
            else blank = 0;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
