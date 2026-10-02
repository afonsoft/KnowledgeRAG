using System.Net;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// WebPage connector (SPEC-20260914-webpage-docfile-connectors RF-001/RF-004):
/// fetches public pages, extracts the main content, optionally crawls same-origin
/// links (depth ≤ 2, ≤ 100 pages, 250 ms politeness). SSRF guard blocks
/// private/loopback targets unless <c>allowPrivateHosts</c> is set.
/// </summary>
public sealed class WebPageConnector(
    IHttpClientFactory httpClientFactory,
    ILogger<WebPageConnector> logger) : ISourceConnector
{
    private static readonly TimeSpan Politeness = TimeSpan.FromMilliseconds(250);

    public SourceType Type => SourceType.WebPage;

    /// <summary>Resolved crawl parameters for one fetch.</summary>
    private sealed record CrawlConfig(
        Uri Start, bool AllowPrivate, int CrawlDepth, int MaxPages,
        string? IncludeSelector, string? ExcludeSelector);

    public async Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var url = config.String("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"WebPage source '{source.Name}' has no valid 'url'");

        var crawl = new CrawlConfig(
            start,
            config.Bool("allowPrivateHosts"),
            config.Int("crawlDepth", 0, 0, 2),
            config.Int("maxPages", 20, 1, 100),
            config.String("includeSelector"),
            config.String("excludeSelector"));

        if (!crawl.AllowPrivate)
            await GuardPublicAsync(start, cancellationToken);

        var robots = await FetchRobotsAsync(start, crawl.AllowPrivate, cancellationToken);
        return await CrawlAsync(crawl, robots, cancellationToken);
    }

    /// <summary>BFS crawl from <see cref="CrawlConfig.Start"/> bounded by
    /// depth/pages/robots with a politeness delay between requests.</summary>
    private async Task<FetchResult> CrawlAsync(
        CrawlConfig crawl, RobotsPolicy robots, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Uri Uri, int Depth)>();
        queue.Enqueue((crawl.Start, 0));
        visited.Add(crawl.Start.GetLeftPart(UriPartial.Path));

        var documents = new List<RawDocument>();
        var warnings = new List<string>();
        var client = httpClientFactory.CreateClient("webpage");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Knowledge/1.0");

        while (queue.Count > 0 && documents.Count < crawl.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, depth) = queue.Dequeue();

            if (!robots.Allows(page))
            {
                logger.LogInformation("robots.txt disallows {Url} — skipped", page);
                warnings.Add($"{page}: disallowed by robots.txt");
                continue;
            }

            var html = await FetchPageHtmlAsync(client, page, crawl.AllowPrivate, warnings, cancellationToken);
            if (html is not null)
                ProcessPage(html, page, depth, crawl, queue, visited, documents);

            if (queue.Count > 0)
                await Task.Delay(Politeness, cancellationToken);
        }

        return new FetchResult(documents, warnings);
    }

    /// <summary>GETs one page; returns its HTML only for 2xx text/html —
    /// fetch failures land in warnings, never abort the crawl.</summary>
    private async Task<string?> FetchPageHtmlAsync(
        HttpClient client, Uri page, bool allowPrivate,
        List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, page);
            if (allowPrivate)
                pageRequest.Options.Set(Security.EgressPolicyHandler.AllowPrivateHostsKey, true);
            using var response = await client.SendAsync(pageRequest, cancellationToken);
            return response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType == "text/html"
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Fetch failed for {Url} — skipped", page);
            warnings.Add($"{page}: fetch failed ({ex.Message})");
            return null;
        }
    }

    /// <summary>Extracts text (a document when non-empty) and enqueues
    /// same-origin links within the crawl depth.</summary>
    private static void ProcessPage(
        string html, Uri page, int depth, CrawlConfig crawl,
        Queue<(Uri Uri, int Depth)> queue, HashSet<string> visited, List<RawDocument> documents)
    {
        var text = HtmlTextExtractor.Extract(html, crawl.IncludeSelector, crawl.ExcludeSelector);
        if (text.Length > 0)
        {
            var title = HtmlTextExtractor.ExtractTitle(html) ?? page.AbsolutePath.Trim('/');
            documents.Add(new RawDocument(page.AbsoluteUri, title, text));
        }

        if (depth >= crawl.CrawlDepth)
            return;
        foreach (var link in HtmlTextExtractor.ExtractLinks(html, page))
        {
            if (visited.Add(link) && visited.Count <= crawl.MaxPages)
                queue.Enqueue((new Uri(link), depth + 1));
        }
    }

    /// <summary>SSRF guard: refuse loopback/link-local/private targets.</summary>
    public static async Task GuardPublicAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot resolve host '{uri.Host}'", ex);
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new InvalidOperationException(
                $"WebPage url '{uri.Host}' resolves to a private/loopback address — " +
                "set 'allowPrivateHosts': true in the source configuration to opt in");
    }

    public static bool IsPrivate(IPAddress ip) =>
        IPAddress.IsLoopback(ip)
        || ip.IsIPv6LinkLocal
        || (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && (ip.GetAddressBytes()[0] & 0xFE) == 0xFC) // fc00::/7 unique-local
        || (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && ip.GetAddressBytes() is { } b
            && (b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)));

    private async Task<RobotsPolicy> FetchRobotsAsync(Uri start, bool allowPrivate, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("webpage");
            var robotsUri = new Uri($"{start.Scheme}://{start.Authority}/robots.txt");
            using var request = new HttpRequestMessage(HttpMethod.Get, robotsUri);
            if (allowPrivate)
                request.Options.Set(Security.EgressPolicyHandler.AllowPrivateHostsKey, true);
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? RobotsPolicy.Parse(await response.Content.ReadAsStringAsync(ct))
                : RobotsPolicy.AllowAll;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return RobotsPolicy.AllowAll;
        }
    }

    /// <summary>Minimal robots.txt: honor the `User-agent: *` group's Disallow prefixes.</summary>
    public sealed class RobotsPolicy
    {
        public static readonly RobotsPolicy AllowAll = new([]);

        private readonly List<string> _disallow;
        private RobotsPolicy(List<string> disallow) => _disallow = disallow;

        public static RobotsPolicy Parse(string robotsTxt)
        {
            var disallow = new List<string>();
            var appliesToUs = false;
            foreach (var rawLine in robotsTxt.Split('\n'))
            {
                var line = rawLine.Split('#')[0].Trim();
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var field = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                    appliesToUs = value is "*" or "Knowledge";
                else if (appliesToUs && field.Equals("Disallow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                    disallow.Add(value);
            }
            return new RobotsPolicy(disallow);
        }

        public bool Allows(Uri page) =>
            !_disallow.Any(d => page.AbsolutePath.StartsWith(d, StringComparison.OrdinalIgnoreCase));
    }
}
