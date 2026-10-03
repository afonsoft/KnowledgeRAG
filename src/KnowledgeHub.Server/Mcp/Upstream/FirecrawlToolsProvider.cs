using System.Text.Json.Nodes;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// Firecrawl proxy tools (SPEC-20260916-firecrawl-mcp-proxy RF-003) — hybrid
/// registration: a static core set is always listed while Enabled (so calls
/// without a key produce a friendly isError), and when an effective API key
/// exists the upstream <c>tools/list</c> is merged in — names and inputSchema
/// byte-identical to Firecrawl, no aliases, except for a root-level
/// <c>examples</c> annotation injected from <see cref="DynamicToolExamples"/>
/// when the upstream omits one (Playground fill). Dynamic entries are
/// authoritative on name collisions.
/// </summary>
public sealed class FirecrawlToolsProvider(
    FirecrawlUpstreamClient upstream,
    IOptions<FirecrawlOptions> options,
    ILogger<FirecrawlToolsProvider> logger) : IToolProvider
{
    private const string ToolCrawl = "firecrawl_crawl";
    private const string IdJobExampleJson = """[{"id":"550e8400-e29b-41d4-a716-446655440000"}]""";

    internal const string NoKeyMessage =
        "Firecrawl API key not configured — open Settings (/settings) or set Firecrawl__ApiKey.";

    /// <summary>Upstream tools that spend credits or mutate remote state — they
    /// keep the Playground write-confirm gate when the upstream omits hints.</summary>
    private static readonly string[] MutatingPrefixes =
    [
        ToolCrawl, "firecrawl_interact", "firecrawl_agent",
        "firecrawl_monitor_", "firecrawl_feedback", "firecrawl_search_feedback"
    ];

    private readonly FirecrawlOptions _options = options.Value;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    // Perf pass (2026-10-03): tools list + expiry travel in ONE reference so
    // reads/invalidation are a single atomic swap — no lock on either path.
    private ToolsCacheSnapshot? _toolsCache;
    private sealed record ToolsCacheSnapshot(IReadOnlyList<CatalogTool> Tools, DateTimeOffset ExpiresAt);

    /// <summary>Test seam — supplies upstream protocol tools without a live
    /// <see cref="McpClient"/> session.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Tool>>>? DynamicToolsSource { get; set; }

    /// <summary>Curated Playground examples (RF-006 root <c>examples</c>) for
    /// the known upstream tools — upstream schemas carry none, and dynamic
    /// entries override the static core, so without this every dynamic tool
    /// would fall back to the generic skeleton and produce invalid calls.
    /// Values are JSON arrays of complete argument objects, validated against
    /// the upstream tools/list schemas.</summary>
    private static readonly IReadOnlyDictionary<string, string> DynamicToolExamples =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["firecrawl_scrape"] = """[{"url":"https://example.com","formats":["markdown"],"onlyMainContent":true}]""",
            ["firecrawl_search"] = """[{"query":"latest .NET 10 release notes","limit":5}]""",
            ["firecrawl_map"] = """[{"url":"https://docs.firecrawl.dev","limit":20}]""",
            [ToolCrawl] = """[{"url":"https://docs.firecrawl.dev","limit":5}]""",
            ["firecrawl_check_crawl_status"] = IdJobExampleJson,
            ["firecrawl_parse"] = """[{"filePath":"./report.pdf","parsers":["pdf"]}]""",
            ["firecrawl_search_feedback"] = """[{"searchId":"550e8400-e29b-41d4-a716-446655440000","rating":"good","valuableSources":[{"url":"https://example.com/article","reason":"answered the query directly"}]}]""",
            ["firecrawl_feedback"] = """[{"endpoint":"scrape","jobId":"550e8400-e29b-41d4-a716-446655440000","rating":"good","note":"extracted content was complete"}]""",
            ["firecrawl_agent"] = """[{"prompt":"Extract the pricing tiers from this page","urls":["https://www.firecrawl.dev/pricing"]}]""",
            ["firecrawl_agent_status"] = IdJobExampleJson,
            ["firecrawl_interact"] = """[{"url":"https://example.com","prompt":"Click the sign-in button"}]""",
            ["firecrawl_interact_stop"] = """[{"scrapeId":"550e8400-e29b-41d4-a716-446655440000"}]""",
            ["firecrawl_monitor_create"] = """[{"pages":["https://docs.firecrawl.dev"],"name":"docs-watch","scheduleText":"every day"}]""",
            ["firecrawl_monitor_list"] = """[{"limit":10,"offset":0}]""",
            ["firecrawl_monitor_get"] = IdJobExampleJson,
            ["firecrawl_monitor_update"] = """[{"id":"550e8400-e29b-41d4-a716-446655440000","body":{"name":"renamed-monitor"}}]""",
            ["firecrawl_monitor_delete"] = IdJobExampleJson,
            ["firecrawl_monitor_run"] = IdJobExampleJson,
            ["firecrawl_monitor_checks"] = """[{"id":"550e8400-e29b-41d4-a716-446655440000","limit":10}]""",
            ["firecrawl_monitor_check"] = """[{"id":"550e8400-e29b-41d4-a716-446655440000","checkId":"660e8400-e29b-41d4-a716-446655440001"}]""",
            ["firecrawl_research_search_papers"] = """[{"query":"retrieval augmented generation","k":5}]""",
            ["firecrawl_research_inspect_paper"] = """[{"paperId":"2103.00020"}]""",
            ["firecrawl_research_related_papers"] = """[{"seed_ids":["2103.00020"],"intent":"similar work on retrieval augmented generation","mode":"similar","k":5}]""",
            ["firecrawl_research_read_paper"] = """[{"paperId":"2103.00020","question":"What is the main contribution?","k":5}]""",
            ["firecrawl_developer_search"] = """[{"query":"blazor websocket reconnect pattern","k":5}]"""
        };

    private static readonly JsonObject ScrapeSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "url":{"type":"string","description":"URL to scrape",
                 "examples":["https://example.com"]},
          "formats":{"type":"array","items":{"type":"string"},
                     "description":"Output formats (e.g. markdown, html, json)"},
          "onlyMainContent":{"type":"boolean","description":"Exclude nav/footer boilerplate"}
        },"required":["url"],
        "examples":[{"url":"https://example.com"},
                    {"url":"https://docs.firecrawl.dev","formats":["markdown"],"onlyMainContent":true}]}
        """)!.AsObject();

    private static readonly JsonObject SearchSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "query":{"type":"string","description":"Search query",
                   "examples":["latest .NET 10 release notes"]},
          "limit":{"type":"integer","description":"Max results"}
        },"required":["query"],
        "examples":[{"query":"latest .NET 10 release notes"}]}
        """)!.AsObject();

    private static readonly JsonObject MapSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "url":{"type":"string","description":"Site URL to map",
                 "examples":["https://docs.firecrawl.dev"]}
        },"required":["url"],
        "examples":[{"url":"https://docs.firecrawl.dev"}]}
        """)!.AsObject();

    private static readonly JsonObject CrawlSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "url":{"type":"string","description":"Starting URL",
                 "examples":["https://docs.firecrawl.dev"]},
          "limit":{"type":"integer","description":"Max pages to crawl"}
        },"required":["url"],
        "examples":[{"url":"https://docs.firecrawl.dev","limit":5}]}
        """)!.AsObject();

    private static readonly JsonObject CrawlStatusSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "id":{"type":"string","description":"Crawl job id",
                "examples":["550e8400-e29b-41d4-a716-446655440000"]}
        },"required":["id"],
        "examples":[{"id":"550e8400-e29b-41d4-a716-446655440000"}]}
        """)!.AsObject();

    private static readonly JsonObject ParseSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "url":{"type":"string","description":"Public URL of the document to parse",
                 "examples":["https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf"]},
          "filePath":{"type":"string","description":"Local file path (two-phase upload flow)"}
        },
        "examples":[{"url":"https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf"}]}
        """)!.AsObject();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!_options.Enabled
            // Runtime toggle (Settings → Integrações): disabled providers
            // contribute nothing to tools/list; stale tools/call hits the
            // generic unknown-tool error.
            || (services.GetService<Settings.IIntegrationStateService>() is { } state
                && !await state.IsEnabledAsync(Settings.IntegrationProviders.Firecrawl, cancellationToken)))
            return [];

        var tools = new Dictionary<string, CatalogTool>(StringComparer.Ordinal);
        foreach (var tool in StaticCoreTools())
            tools[tool.Name] = tool;

        // Dynamic merge only makes sense when an upstream session can authenticate.
        if (await upstream.HasApiKeyAsync(cancellationToken))
            foreach (var tool in await GetDynamicToolsAsync(cancellationToken))
                tools[tool.Name] = tool;

        return [.. tools.Values];
    }

    /// <summary>Drops the cached upstream tools/list — called when the
    /// integration secret changes via Settings.</summary>
    public void InvalidateToolsCache()
    {
        // Lock-free: the next reader sees a null snapshot and re-fetches —
        // no semaphore Wait() blocking a thread-pool thread on the settings path.
        Interlocked.Exchange(ref _toolsCache, null);
    }

    private async Task<IReadOnlyList<CatalogTool>> GetDynamicToolsAsync(CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _toolsCache);
        if (snapshot is not null && DateTimeOffset.UtcNow < snapshot.ExpiresAt)
            return snapshot.Tools;

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _toolsCache;
            if (snapshot is not null && DateTimeOffset.UtcNow < snapshot.ExpiresAt)
                return snapshot.Tools;

            try
            {
                var protocolTools = DynamicToolsSource is { } source
                    ? await source(cancellationToken)
                    : (await upstream.ListToolsAsync(cancellationToken))
                        .Select(t => t.ProtocolTool).ToList();
                _toolsCache = new ToolsCacheSnapshot(
                    protocolTools.Select(MapTool).ToList(),
                    DateTimeOffset.UtcNow.AddSeconds(_options.ToolsCacheSeconds));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // last-known-good wins; empty when nothing was ever fetched.
                logger.LogWarning(ex, "firecrawl tools/list failed — serving cached/static set");
            }
            return _toolsCache?.Tools ?? [];
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private CatalogTool MapTool(Tool proto)
    {
        var schema = JsonNode.Parse(proto.InputSchema.GetRawText()) as JsonObject
            ?? new JsonObject { ["type"] = "object" };
        var name = proto.Name;
        if (!schema.ContainsKey("examples") &&
            DynamicToolExamples.TryGetValue(name, out var examplesJson))
            schema["examples"] = JsonNode.Parse(examplesJson);
        return new CatalogTool
        {
            Name = name,
            Description = proto.Description ?? $"Firecrawl tool {name} (proxied).",
            InputSchema = schema,
            ReadOnly = proto.Annotations?.ReadOnlyHint
                ?? !MutatingPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
            DestructiveHint = proto.Annotations?.DestructiveHint,
            IdempotentHint = proto.Annotations?.IdempotentHint,
            OpenWorldHint = true,
            Title = proto.Annotations?.Title,
            Handler = (ctx, ct) => DispatchAsync(name, ctx, ct)
        };
    }

    /// <summary>Shared dispatch: friendly isError without a key, transparent
    /// passthrough with one (RF-005 / CA-002). The caller's per-key secret wins
    /// over the global store/env resolution (SPEC-20260922 RF-002).</summary>
    private async ValueTask<CallToolResult> DispatchAsync(
        string toolName, ToolCallContext ctx, CancellationToken ct)
    {
        var overrideKey = await ResolveOverrideAsync(ctx, ct);
        if (overrideKey is null && !await upstream.HasApiKeyAsync(ct))
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = NoKeyMessage }]
            };
        return await upstream.CallAsync(toolName, ctx.Arguments, ct, overrideKey);
    }

    /// <summary>Per-key effective secret for the calling API key, or null for
    /// non-apikey callers (cookie sessions keep global resolution).</summary>
    private static async Task<string?> ResolveOverrideAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var keyId = CallerIdentity.TryGetApiKeyId(ctx);
        if (keyId is null)
            return null;
        var settings = ctx.Services.GetRequiredService<IApiKeyChatSettingsService>();
        return await settings.GetIntegrationSecretAsync(keyId.Value, IntegrationProviders.Firecrawl, ct);
    }

    private IReadOnlyList<CatalogTool> StaticCoreTools() =>
    [
        new CatalogTool
        {
            Name = "firecrawl_scrape",
            Title = "Scrape URL (Firecrawl)",
            Description = "Scrape a single URL — content or structured fields (proxied to Firecrawl).",
            InputSchema = ScrapeSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("firecrawl_scrape", ctx, ct)
        },
        new CatalogTool
        {
            Name = "firecrawl_search",
            Title = "Web search (Firecrawl)",
            Description = "Search the web (proxied to Firecrawl).",
            InputSchema = SearchSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("firecrawl_search", ctx, ct)
        },
        new CatalogTool
        {
            Name = "firecrawl_map",
            Title = "Map site (Firecrawl)",
            Description = "Discover site URLs before extraction (proxied to Firecrawl).",
            InputSchema = MapSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("firecrawl_map", ctx, ct)
        },
        new CatalogTool
        {
            Name = ToolCrawl,
            Title = "Crawl site (Firecrawl)",
            Description = "Crawl a site/section; polls the job to a terminal state (proxied to Firecrawl — billable).",
            InputSchema = CrawlSchema,
            ReadOnly = false,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync(ToolCrawl, ctx, ct)
        },
        new CatalogTool
        {
            Name = "firecrawl_check_crawl_status",
            Title = "Crawl status (Firecrawl)",
            Description = "Check/resume a running crawl job (proxied to Firecrawl).",
            InputSchema = CrawlStatusSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("firecrawl_check_crawl_status", ctx, ct)
        },
        new CatalogTool
        {
            Name = "firecrawl_parse",
            Title = "Parse file (Firecrawl)",
            Description = "Parse a PDF/document/spreadsheet/HTML file (proxied to Firecrawl).",
            InputSchema = ParseSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("firecrawl_parse", ctx, ct)
        }
    ];
}
