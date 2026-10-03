using System.Text.Json.Nodes;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// Tavily proxy tools (SPEC-20260916-tavily-mcp-proxy RF-003) — hybrid
/// registration: a static core set is always listed while Enabled (so calls
/// without a key produce a friendly isError), and when an effective API key
/// exists the upstream <c>tools/list</c> is merged in — names and inputSchema
/// byte-identical to Tavily, no aliases, except for a root-level
/// <c>examples</c> annotation injected from <see cref="DynamicToolExamples"/>
/// when the upstream omits one (Playground fill). Dynamic entries are
/// authoritative on name collisions.
/// </summary>
public sealed class TavilyToolsProvider(
    TavilyUpstreamClient upstream,
    IOptions<TavilyOptions> options,
    ILogger<TavilyToolsProvider> logger) : IToolProvider
{
    private const string ToolCrawl = "tavily_crawl";
    private const string ToolResearch = "tavily_research";

    internal const string NoKeyMessage =
        "Tavily API key not configured — open Settings (/settings) or set Tavily__ApiKey.";

    /// <summary>Upstream tools that spend credits or run long jobs — they keep
    /// the Playground write-confirm gate when the upstream omits hints.</summary>
    private static readonly string[] MutatingPrefixes =
    [
        ToolCrawl, ToolResearch
    ];

    private readonly TavilyOptions _options = options.Value;
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
    /// Values are JSON arrays of complete argument objects.</summary>
    private static readonly IReadOnlyDictionary<string, string> DynamicToolExamples =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tavily_search"] = """[{"query":"latest .NET 10 release notes","max_results":5,"search_depth":"basic"}]""",
            ["tavily_extract"] = """[{"urls":["https://example.com"]}]""",
            ["tavily_map"] = """[{"url":"https://docs.tavily.com","max_depth":1,"limit":20}]""",
            [ToolCrawl] = """[{"url":"https://docs.tavily.com","max_depth":1,"limit":5}]""",
            [ToolResearch] = """[{"input":"Compare .NET 10 minimal APIs vs controllers for a small team"}]"""
        };

    private static readonly JsonObject SearchSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "query":{"type":"string","description":"Search query",
                   "examples":["latest .NET 10 release notes"]},
          "max_results":{"type":"integer","description":"Max results (default 5)"},
          "search_depth":{"type":"string","description":"basic | advanced"},
          "include_domains":{"type":"array","items":{"type":"string"}},
          "exclude_domains":{"type":"array","items":{"type":"string"}}
        },"required":["query"],
        "examples":[{"query":"latest .NET 10 release notes"},
                    {"query":"site reliability best practices","max_results":3,"search_depth":"basic"}]}
        """)!.AsObject();

    private static readonly JsonObject ExtractSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "urls":{"type":"array","items":{"type":"string"},
                  "description":"One or more URLs to extract content from",
                  "examples":[["https://example.com"]]}
        },"required":["urls"],
        "examples":[{"urls":["https://example.com"]}]}
        """)!.AsObject();

    private static readonly JsonObject MapSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "url":{"type":"string","description":"Site URL to map",
                 "examples":["https://docs.tavily.com"]},
          "max_depth":{"type":"integer","description":"Max link depth"},
          "limit":{"type":"integer","description":"Max URLs returned"}
        },"required":["url"],
        "examples":[{"url":"https://docs.tavily.com","max_depth":1,"limit":20}]}
        """)!.AsObject();

    private static readonly JsonObject CrawlSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "url":{"type":"string","description":"Starting URL",
                 "examples":["https://docs.tavily.com"]},
          "max_depth":{"type":"integer","description":"Max link depth"},
          "limit":{"type":"integer","description":"Max pages to crawl"},
          "instructions":{"type":"string","description":"Natural-language crawl guidance"}
        },"required":["url"],
        "examples":[{"url":"https://docs.tavily.com","max_depth":1,"limit":5}]}
        """)!.AsObject();

    private static readonly JsonObject ResearchSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "input":{"type":"string","description":"Research question or task",
                   "examples":["Compare .NET 10 minimal APIs vs controllers for a small team"]},
          "model":{"type":"string","description":"Research model (e.g. mini | pro)"}
        },"required":["input"],
        "examples":[{"input":"Compare .NET 10 minimal APIs vs controllers for a small team"}]}
        """)!.AsObject();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!_options.Enabled
            // Runtime toggle (Settings → Integrações): disabled providers
            // contribute nothing to tools/list; stale tools/call hits the
            // generic unknown-tool error.
            || (services.GetService<Settings.IIntegrationStateService>() is { } state
                && !await state.IsEnabledAsync(Settings.IntegrationProviders.Tavily, cancellationToken)))
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
                logger.LogWarning(ex, "tavily tools/list failed — serving cached/static set");
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
            Description = proto.Description ?? $"Tavily tool {name} (proxied).",
            InputSchema = schema,
            ReadOnly = proto.Annotations?.ReadOnlyHint
                ?? !MutatingPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
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
        return await settings.GetIntegrationSecretAsync(keyId.Value, IntegrationProviders.Tavily, ct);
    }

    private IReadOnlyList<CatalogTool> StaticCoreTools() =>
    [
        new CatalogTool
        {
            Name = "tavily_search",
            Title = "Web search (Tavily)",
            Description = "Search the web (proxied to Tavily).",
            InputSchema = SearchSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("tavily_search", ctx, ct)
        },
        new CatalogTool
        {
            Name = "tavily_extract",
            Title = "Extract content (Tavily)",
            Description = "Extract clean content from one or more URLs (proxied to Tavily).",
            InputSchema = ExtractSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("tavily_extract", ctx, ct)
        },
        new CatalogTool
        {
            Name = "tavily_map",
            Title = "Map site (Tavily)",
            Description = "Discover site URLs before extraction (proxied to Tavily).",
            InputSchema = MapSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("tavily_map", ctx, ct)
        },
        new CatalogTool
        {
            Name = ToolCrawl,
            Title = "Crawl site (Tavily)",
            Description = "Crawl a site/section (proxied to Tavily — billable/long-running).",
            InputSchema = CrawlSchema,
            ReadOnly = false,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync(ToolCrawl, ctx, ct)
        },
        new CatalogTool
        {
            Name = ToolResearch,
            Title = "Deep research (Tavily)",
            Description = "Deep research on a question (proxied to Tavily — billable/long-running).",
            InputSchema = ResearchSchema,
            ReadOnly = false,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync(ToolResearch, ctx, ct)
        }
    ];
}
