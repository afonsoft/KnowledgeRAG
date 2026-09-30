using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// RSS/Atom feed connector (SPEC-20260927-rss-feed-connector RF-003/RF-004/RF-005):
/// fetches an RSS 2.0 or Atom 1.0 feed via HTTP GET, parses it with
/// <see cref="FeedParser"/>, maps items to <see cref="RawDocument"/>, and
/// supports incremental sync by fingerprint (<c>rss:{guid}</c> or
/// <c>rss:{sha256(link+pubDateTicks)}</c>). Optional <c>fetchFullContent</c>
/// downloads each item's linked page and extracts text via
/// <see cref="HtmlTextExtractor"/>. Errors are sanitized — Basic auth
/// credentials in URLs are redacted to <c>***@</c>.
/// </summary>
public sealed partial class RssFeedConnector(
    IHttpClientFactory httpClientFactory,
    ILogger<RssFeedConnector> logger) : IIncrementalSourceConnector, IItemFetchConnector
{
    public SourceType Type => SourceType.RssFeed;

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var feedUrl = config.String("feedUrl");
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"RssFeed source '{source.Name}' has no valid 'feedUrl' (http/https)");

        var maxItems = config.Int("maxItems", 100, 1, 500);
        var fetchFullContent = config.Bool("fetchFullContent");
        var forceRefresh = config.Bool("forceRefresh");

        var xml = await FetchFeedAsync(feedUrl, cancellationToken);
        List<FeedParser.FeedItem> items;
        try
        {
            items = FeedParser.Parse(xml).ToList();
        }
        catch (FeedParseException ex)
        {
            throw new InvalidOperationException(
                $"RssFeed source '{source.Name}': {ex.Message}", ex);
        }

        var documents = new List<RawDocument>();
        var warnings = new List<string>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        for (var i = 0; i < items.Count; i++)
        {
            if (documents.Count >= maxItems)
            {
                truncated = true;
                warnings.Add($"truncated at maxItems={maxItems} ({items.Count} items in feed)");
                break;
            }

            var item = items[i];
            var uriRef = NormalizeUri(item.Link) ?? item.Guid ?? $"rss:{Sha256(item.Title + item.Content)}";
            if (seenUris.Contains(uriRef))
                continue;
            seenUris.Add(uriRef);

            var fingerprint = ComputeFingerprint(item);

            // Incremental: if the fingerprint matches, emit a stub.
            if (!forceRefresh
                && existingFingerprints.TryGetValue(uriRef, out var stored)
                && stored == fingerprint)
            {
                documents.Add(new RawDocument(uriRef, item.Title, "", fingerprint));
                continue;
            }

            var content = BuildContent(item, feedUrl);
            if (fetchFullContent && item.Link is not null)
            {
                try
                {
                    var fullText = await FetchFullContentAsync(item.Link, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(fullText))
                        content = fullText;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"full content fetch failed for '{item.Link}': {ex.Message} — using feed content");
                }
            }

            documents.Add(new RawDocument(uriRef, item.Title, content, fingerprint));
        }

        if (documents.Count == 0 && items.Count > 0)
            logger.LogWarning("RssFeed source {SourceId} mapped no documents from {ItemCount} feed items", source.Id, items.Count);

        return new FetchResult(documents, warnings, Truncated: truncated);
    }

    public async Task<RawDocument?> FetchItemAsync(
        KnowledgeSource source, string uriReference, CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var feedUrl = config.String("feedUrl");
        if (feedUrl is null)
            return null;

        var xml = await FetchFeedAsync(feedUrl, cancellationToken);
        List<FeedParser.FeedItem> items;
        try
        {
            items = FeedParser.Parse(xml).ToList();
        }
        catch (FeedParseException)
        {
            return null;
        }

        foreach (var item in items)
        {
            var uriRef = NormalizeUri(item.Link) ?? item.Guid ?? $"rss:{Sha256(item.Title + item.Content)}";
            if (string.Equals(uriRef, uriReference, StringComparison.OrdinalIgnoreCase))
            {
                var content = BuildContent(item, feedUrl);
                return new RawDocument(uriRef, item.Title, content, ComputeFingerprint(item));
            }
        }

        return null;
    }

    private async Task<string> FetchFeedAsync(string feedUrl, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("feed");
        try
        {
            using var response = await http.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode == 429)
            {
                var retryAfter = RetryAfterSeconds(response);
                if (retryAfter is null)
                    throw new InvalidOperationException(
                        $"feed returned HTTP 429 sem Retry-After — desistindo após 1 tentativa");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfter.Value, 10)), ct);
                using var retry = await http.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!retry.IsSuccessStatusCode)
                    throw FeedError(feedUrl, retry.StatusCode);
                return await retry.Content.ReadAsStringAsync(ct);
            }

            if (!response.IsSuccessStatusCode)
                throw FeedError(feedUrl, response.StatusCode);

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"feed fetch failed: {SanitizeUrl(feedUrl)} — {ex.Message}", ex);
        }
    }

    private async Task<string> FetchFullContentAsync(string itemUrl, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("feed");
        using var response = await http.GetAsync(itemUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync(ct);
        return HtmlTextExtractor.Extract(html);
    }

    private static string BuildContent(FeedParser.FeedItem item, string feedUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {item.Title}");
        sb.AppendLine($"Fonte: {feedUrl}");
        if (item.PublishedAt is not null)
            sb.AppendLine($"Publicado: {item.PublishedAt:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine();
        sb.Append(item.Content);
        return sb.ToString();
    }

    private static string ComputeFingerprint(FeedParser.FeedItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Guid))
            return $"rss:{item.Guid}";
        var key = item.Link ?? "";
        if (item.PublishedAt is not null)
            key += item.PublishedAt.Value.Ticks;
        return $"rss:{Sha256(key)}";
    }

    /// <summary>Canonicalizes a URL to lowercase host + drops fragment for dedup.</summary>
    private static string? NormalizeUri(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return link;
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.ToString().ToLowerInvariant();
    }

    private static int? RetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw is not null && int.TryParse(raw, out var seconds) && seconds >= 0)
                return seconds;
        }
        return null;
    }

    private static InvalidOperationException FeedError(string feedUrl, HttpStatusCode status) =>
        new($"feed returned HTTP {(int)status} — {SanitizeUrl(feedUrl)}");

    /// <summary>Redacts Basic auth credentials (user:pass@) in URLs for safe error messages.</summary>
    [GeneratedRegex(@"//[^/@]+@")]
    private static partial Regex UserInfoPattern();

    private static string SanitizeUrl(string url) =>
        UserInfoPattern().Replace(url, "//***@");

    private static string Sha256(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
