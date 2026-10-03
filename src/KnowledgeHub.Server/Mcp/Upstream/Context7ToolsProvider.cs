using System.Text.Json.Nodes;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// Context7 proxy tools (SPEC-20260922-context7-mcp-proxy RF-003) — hybrid
/// registration: a static core set is always listed while Enabled (so calls
/// without a key produce a friendly isError), and when an effective API key
/// exists the upstream <c>tools/list</c> is merged in — names and inputSchema
/// byte-identical to Context7, no aliases, except for a root-level
/// <c>examples</c> annotation injected from <see cref="DynamicToolExamples"/>
/// when the upstream omits one (Playground fill). Dynamic entries are
/// authoritative on name collisions.
/// </summary>
public sealed class Context7ToolsProvider(
    Context7UpstreamClient upstream,
    IOptions<Context7Options> options,
    ILogger<Context7ToolsProvider> logger) : IToolProvider
{
    internal const string NoKeyMessage =
        "Context7 API key not configured — open Settings (/settings) or set Context7__ApiKey.";

    private readonly Context7Options _options = options.Value;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    // Perf pass (2026-10-03): tools list + expiry travel in ONE reference so
    // reads/invalidation are a single atomic swap — no lock on either path.
    private ToolsCacheSnapshot? _toolsCache;
    private sealed record ToolsCacheSnapshot(IReadOnlyList<CatalogTool> Tools, DateTimeOffset ExpiresAt);

    /// <summary>Test seam — supplies upstream protocol tools without a live
    /// <see cref="McpClient"/> session.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Tool>>>? DynamicToolsSource { get; set; }

    /// <summary>Curated Playground examples (root <c>examples</c>) for the
    /// known upstream tools — upstream schemas carry none, and dynamic entries
    /// override the static core, so without this every dynamic tool would fall
    /// back to the generic skeleton and produce invalid calls. Values are JSON
    /// arrays of complete argument objects.</summary>
    private static readonly IReadOnlyDictionary<string, string> DynamicToolExamples =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["resolve-library-id"] = """[{"libraryName":"Next.js","query":"app router routing"},{"libraryName":"MongoDB","query":"connection pooling"}]""",
            ["query-docs"] = """[{"libraryId":"/vercel/next.js","query":"middleware authentication"},{"libraryId":"/mongodb/docs","query":"connection string format"}]"""
        };

    private static readonly JsonObject ResolveLibraryIdSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "query":{"type":"string","description":"What to look up in the library's documentation — used to rank results by relevance",
                   "examples":["app router routing"]},
          "libraryName":{"type":"string","description":"Official library name (e.g. 'Next.js', 'MongoDB', 'Three.js')",
                         "examples":["Next.js"]}
        },"required":["query","libraryName"],
        "examples":[{"libraryName":"Next.js","query":"app router routing"},
                    {"libraryName":"MongoDB","query":"connection pooling"}]}
        """)!.AsObject();

    private static readonly JsonObject QueryDocsSchema = JsonNode.Parse("""
        {"type":"object","additionalProperties":true,"properties":{
          "libraryId":{"type":"string","description":"Exact Context7 library ID (/org/project[/version]) — from resolve-library-id or the user query",
                       "examples":["/vercel/next.js"]},
          "query":{"type":"string","description":"Single-concept question to look up in the library docs",
                   "examples":["middleware authentication"]}
        },"required":["libraryId","query"],
        "examples":[{"libraryId":"/vercel/next.js","query":"middleware authentication"},
                    {"libraryId":"/mongodb/docs","query":"connection string format"}]}
        """)!.AsObject();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!_options.Enabled
            // Runtime toggle (Settings → Integrações): disabled providers
            // contribute nothing to tools/list; stale tools/call hits the
            // generic unknown-tool error.
            || (services.GetService<Settings.IIntegrationStateService>() is { } state
                && !await state.IsEnabledAsync(Settings.IntegrationProviders.Context7, cancellationToken)))
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
                logger.LogWarning(ex, "context7 tools/list failed — serving cached/static set");
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
            Description = proto.Description ?? $"Context7 tool {name} (proxied).",
            InputSchema = schema,
            // Context7 exposes no write tools — absent upstream hints default
            // to read-only.
            ReadOnly = proto.Annotations?.ReadOnlyHint ?? true,
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
        return await settings.GetIntegrationSecretAsync(keyId.Value, IntegrationProviders.Context7, ct);
    }

    private IReadOnlyList<CatalogTool> StaticCoreTools() =>
    [
        new CatalogTool
        {
            Name = "resolve-library-id",
            Title = "Resolve library ID (Context7)",
            Description = "Resolve a library/package name to a Context7 library ID (proxied to Context7).",
            InputSchema = ResolveLibraryIdSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("resolve-library-id", ctx, ct)
        },
        new CatalogTool
        {
            Name = "query-docs",
            Title = "Query docs (Context7)",
            Description = "Fetch up-to-date, version-specific documentation for a Context7 library ID (proxied to Context7).",
            InputSchema = QueryDocsSchema,
            ReadOnly = true,
            IdempotentHint = true,
            OpenWorldHint = true,
            Handler = (ctx, ct) => DispatchAsync("query-docs", ctx, ct)
        }
    ];
}
