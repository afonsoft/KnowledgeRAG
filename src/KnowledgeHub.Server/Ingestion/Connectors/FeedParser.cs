using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// RSS 2.0 + Atom 1.0 parser (SPEC-20260927-rss-feed-connector RF-002):
/// namespace-tolerant (LocalName matching covers <c>content:encoded</c>,
/// <c>dc:date</c>, <c>atom:</c> prefixed elements), HTML stripped via
/// <see cref="HtmlTextExtractor"/>. Dates parsed from RFC 822 (RSS pubDate),
/// ISO 8601 (Atom published/updated, dc:date) and <c>dc:date</c>. Invalid
/// dates silently become null. Unknown root or malformed XML throws
/// <see cref="FeedParseException"/>.
/// </summary>
public static partial class FeedParser
{
    /// <summary>Parsed feed item before mapping to <see cref="RawDocument"/>.</summary>
    public sealed record FeedItem(string? Guid, string Title, string? Link, string Content, DateTimeOffset? PublishedAt);

    /// <summary>Parse a raw XML feed string into feed items.</summary>
    public static IReadOnlyList<FeedItem> Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FeedParseException("XML inválido — não é um feed RSS 2.0 ou Atom válido", ex);
        }

        var root = doc.Root ?? throw new FeedParseException("XML sem elemento raiz");
        var rootName = root.Name.LocalName;

        // RSS 2.0: <rss><channel><item>...</item></channel></rss>
        // Also handle <rdf:RDF> with items directly under root.
        if (rootName is "rss" or "RDF" or "rdf")
        {
            var channel = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "channel") ?? root;
            return ParseRssItems(channel.Descendants().Where(e => e.Name.LocalName == "item"));
        }

        // Atom 1.0: <feed xmlns="http://www.w3.org/2005/Atom"><entry>...</entry></feed>
        if (rootName == "feed")
        {
            return ParseAtomItems(root.Descendants().Where(e => e.Name.LocalName == "entry"));
        }

        throw new FeedParseException(
            $"formato de feed não suportado — raiz '{rootName}' (esperado RSS 2.0 ou Atom 1.0)");
    }

    private static List<FeedItem> ParseRssItems(IEnumerable<XElement> items) =>
        items.Select(ParseRssItem).Where(i => i is not null).Select(i => i!).ToList();

    private static List<FeedItem> ParseAtomItems(IEnumerable<XElement> entries) =>
        entries.Select(ParseAtomEntry).Where(i => i is not null).Select(i => i!).ToList();

    private static FeedItem? ParseRssItem(XElement item)
    {
        var title = ChildValue(item, "title");
        var link = ChildValue(item, "link");
        var guid = ChildValue(item, "guid");

        // Prefer content:encoded, fall back to description
        var content = ChildValue(item, "encoded") ?? ChildValue(item, "description") ?? "";
        content = StripHtml(content);

        var publishedAt = ParseDate(ChildValue(item, "pubDate"))
            ?? ParseDate(ChildValue(item, "date"));

        // Skip items that have no identifying field at all
        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(link)
            && string.IsNullOrWhiteSpace(guid))
            return null;

        title = string.IsNullOrWhiteSpace(title) ? (link ?? guid ?? "item") : title;
        return new FeedItem(guid, title, link, content, publishedAt);
    }

    private static FeedItem? ParseAtomEntry(XElement entry)
    {
        var title = ChildValue(entry, "title");

        // Prefer rel="alternate" link, fall back to first link
        var link = entry.Elements()
            .Where(e => e.Name.LocalName == "link")
            .OrderByDescending(e => e.Attribute("rel")?.Value == "alternate")
            .FirstOrDefault()
            ?.Attribute("href")?.Value;

        var id = ChildValue(entry, "id");

        var content = ChildValue(entry, "content")
            ?? ChildValue(entry, "summary")
            ?? "";
        content = StripHtml(content);

        var publishedAt = ParseDate(ChildValue(entry, "published"))
            ?? ParseDate(ChildValue(entry, "updated"));

        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(link)
            && string.IsNullOrWhiteSpace(id))
            return null;

        title = string.IsNullOrWhiteSpace(title) ? (link ?? id ?? "entry") : title;
        return new FeedItem(id, title, link, content, publishedAt);
    }

    /// <summary>Gets the text value of the first child element with the given
    /// local name (namespace-agnostic).</summary>
    private static string? ChildValue(XElement parent, string localName) =>
        parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName)
            ?.Value.Trim();

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        return HtmlTextExtractor.Extract(html);
    }

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var enUs = CultureInfo.GetCultureInfo("en-US");

        // RFC 822 (RSS pubDate): "Mon, 01 Sep 2026 10:00:00 GMT" — the day-of-week
        // prefix ("Mon, ") breaks .NET's generic TryParse, so strip it when present.
        var trimmed = raw.Trim();
        if (trimmed.Length > 4 && trimmed[3] == ',')
            trimmed = trimmed[(trimmed.IndexOf(',') + 1)..].TrimStart();

        if (DateTimeOffset.TryParse(trimmed, enUs,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var rfc822))
            return rfc822;

        // Try ISO 8601 (Atom published/updated, dc:date) with InvariantCulture.
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            return iso;

        return null;
    }
}

/// <summary>Thrown when the feed XML is malformed or not RSS 2.0 / Atom 1.0.</summary>
public sealed class FeedParseException(string message, Exception? inner = null) : Exception(message, inner);
