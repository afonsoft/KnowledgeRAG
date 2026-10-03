using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// DeepWiki proxy tools (SPEC-07 RF-002/RF-003/RF-004) — names are
/// byte-identical to upstream (transparent bypass): ask_question,
/// read_wiki_structure, read_wiki_contents.
/// SPEC-20260917-upstream-tools-passthrough: in private mode (effective API
/// key present) the upstream <c>tools/list</c> is merged in verbatim — names
/// and inputSchema identical to upstream, including the private
/// <c>devin_*</c> tools — while public mode keeps the static core only.
/// Dynamic entries are authoritative on name collisions and cached for
/// <see cref="DeepWikiOptions.ToolsCacheSeconds"/>; last-known-good wins on
/// discovery failures.
/// </summary>
public sealed partial class DeepWikiToolsProvider(
    DeepWikiUpstreamClient upstream,
    IOptions<DeepWikiOptions> options,
    ILogger<DeepWikiToolsProvider> logger) : IToolProvider
{
    internal const string NoKeyMessage =
        "DeepWiki API key not configured — open Settings (/settings) or set DeepWiki__ApiKey.";

    private readonly DeepWikiOptions _options = options.Value;
    private UpstreamToolsCache? _toolsCache;
    private UpstreamToolsCache ToolsCache =>
        _toolsCache ??= new(FetchDynamicToolsAsync, TimeSpan.FromSeconds(options.Value.ToolsCacheSeconds));

    /// <summary>Test seam — supplies upstream protocol tools without a live
    /// <see cref="McpClient"/> session.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Tool>>>? DynamicToolsSource { get; set; }

    private static readonly JsonObject AskSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repoName":{"anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"},"maxItems":10}],
                      "description":"GitHub repo(s) in owner/repo format (max 10)",
                      "examples":["langchain-ai/langgraph"]},
          "question":{"type":"string","description":"Question about the repository",
                      "examples":["How does checkpointing work?"]}
        },"required":["repoName","question"],
        "examples":[
          {"repoName":"langchain-ai/langgraph","question":"How does checkpointing work?"},
          {"repoName":["langchain-ai/langgraph","afonsoft/skills"],"question":"Compare the architectures"}
        ]}
        """)!.AsObject();

    private static readonly JsonObject RepoSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repoName":{"type":"string","description":"GitHub repo in owner/repo format",
                      "examples":["langchain-ai/langgraph"]}
        },"required":["repoName"],
        "examples":[{"repoName":"langchain-ai/langgraph"}]}
        """)!.AsObject();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoNamePattern();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!_options.Enabled
            // Runtime toggle (Settings → Integrações): disabled providers
            // contribute nothing to tools/list; stale tools/call hits the
            // generic unknown-tool error.
            || (services.GetService<Settings.IIntegrationStateService>() is { } state
                && !await state.IsEnabledAsync(Settings.IntegrationProviders.DeepWiki, cancellationToken)))
            return [];

        var tools = new Dictionary<string, CatalogTool>(StringComparer.Ordinal);
        foreach (var tool in StaticCoreTools())
            tools[tool.Name] = tool;

        // Dynamic merge only exists in private mode — without a key the public
        // endpoint exposes the same static set.
        if (await upstream.HasApiKeyAsync(cancellationToken))
            foreach (var tool in await GetDynamicToolsAsync(cancellationToken))
                tools[tool.Name] = tool;

        return [.. tools.Values];
    }

    /// <summary>Drops the cached upstream tools/list — called when the
    /// integration secret changes via Settings.</summary>
    public void InvalidateToolsCache() => _toolsCache?.Invalidate();

    private Task<IReadOnlyList<CatalogTool>> GetDynamicToolsAsync(CancellationToken cancellationToken) =>
        ToolsCache.GetAsync(
            ex => logger.LogWarning(ex, "deepwiki tools/list failed — serving cached/static set"),
            cancellationToken);

    private async Task<IReadOnlyList<CatalogTool>> FetchDynamicToolsAsync(CancellationToken cancellationToken)
    {
        var protocolTools = DynamicToolsSource is { } source
            ? await source(cancellationToken)
            : (await upstream.ListToolsAsync(cancellationToken))
                .Select(t => t.ProtocolTool).ToList();
        return protocolTools.Select(MapTool).ToList();
    }

    private CatalogTool MapTool(Tool proto)
    {
        var schema = JsonNode.Parse(proto.InputSchema.GetRawText()) as JsonObject
            ?? new JsonObject { ["type"] = "object" };
        var name = proto.Name;
        return new CatalogTool
        {
            Name = name,
            Description = proto.Description ?? $"DeepWiki tool {name} (proxied).",
            InputSchema = schema,
            // Absent upstream hint → not read-only: private-mode tools
            // (devin_session_*, …) mutate remote state, so the Playground
            // keeps the write-confirm gate unless upstream says otherwise.
            ReadOnly = proto.Annotations?.ReadOnlyHint ?? false,
            Handler = (ctx, ct) => DispatchAsync(name, ctx, ct)
        };
    }

    /// <summary>Shared dispatch for dynamically discovered tools: friendly
    /// isError when the key was removed after listing, transparent passthrough
    /// otherwise.</summary>
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

    /// <summary>Direct passthrough for the static core tools — works keyless
    /// (public endpoint); the caller's per-key secret, when present, switches
    /// to the private endpoint (SPEC-20260922 RF-002).</summary>
    private async Task<CallToolResult> CallUpstreamAsync(
        string toolName, ToolCallContext ctx, CancellationToken ct)
        => await upstream.CallAsync(toolName, ctx.Arguments, ct, await ResolveOverrideAsync(ctx, ct));

    /// <summary>Per-key effective secret for the calling API key, or null for
    /// non-apikey callers (cookie sessions keep global resolution).</summary>
    private static async Task<string?> ResolveOverrideAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var keyId = CallerIdentity.TryGetApiKeyId(ctx);
        if (keyId is null)
            return null;
        var settings = ctx.Services.GetRequiredService<IApiKeyChatSettingsService>();
        return await settings.GetIntegrationSecretAsync(keyId.Value, IntegrationProviders.DeepWiki, ct);
    }

    private IReadOnlyList<CatalogTool> StaticCoreTools() =>
        [
            new CatalogTool
            {
                Name = "ask_question",
                Title = "Ask question (DeepWiki)",
                Description = "Ask any question about a GitHub repository (proxied to DeepWiki).",
                InputSchema = AskSchema,
                ReadOnly = true,
                IdempotentHint = true,
                OpenWorldHint = true,
                Handler = async (ctx, ct) =>
                {
                    ValidateRepoArg(ctx, out _);
                    ToolArgs.RequiredString(ctx, "question");
                    return await CallUpstreamAsync("ask_question", ctx, ct);
                }
            },
            new CatalogTool
            {
                Name = "read_wiki_structure",
                Title = "Read wiki structure (DeepWiki)",
                Description = "Get the documentation topic list for a GitHub repository (proxied to DeepWiki).",
                InputSchema = RepoSchema,
                ReadOnly = true,
                IdempotentHint = true,
                OpenWorldHint = true,
                Handler = async (ctx, ct) =>
                {
                    ValidateRepoArg(ctx, out _);
                    return await CallUpstreamAsync("read_wiki_structure", ctx, ct);
                }
            },
            new CatalogTool
            {
                Name = "read_wiki_contents",
                Title = "Read wiki contents (DeepWiki)",
                Description = "Read documentation contents for a GitHub repository (proxied to DeepWiki).",
                InputSchema = RepoSchema,
                ReadOnly = true,
                IdempotentHint = true,
                OpenWorldHint = true,
                Handler = async (ctx, ct) =>
                {
                    ValidateRepoArg(ctx, out _);
                    return await CallUpstreamAsync("read_wiki_contents", ctx, ct);
                }
            }
        ];

    /// <summary>
    /// Client-side validation (SPEC-07 RF-004): repoName is a string or
    /// ≤10-element string array, each element matching owner/repo.
    /// </summary>
    public static void ValidateRepoArg(ToolCallContext ctx, out IReadOnlyList<string> repos)
    {
        var arguments = ctx.Arguments;
        if (arguments is null || !arguments.TryGetValue("repoName", out var el))
            throw new McpProtocolException("missing required argument 'repoName'", McpErrorCode.InvalidParams);
        repos = ValidateRepoName(el);
    }

    /// <summary>Pure validation of the repoName JSON value — unit-testable.</summary>
    public static IReadOnlyList<string> ValidateRepoName(JsonElement el)
    {
        List<string> repos = el.ValueKind switch
        {
            JsonValueKind.String => [el.GetString()!],
            JsonValueKind.Array => el.EnumerateArray().Select(e => e.GetString() ?? "").ToList(),
            _ => throw new McpProtocolException("'repoName' must be a string or an array of strings", McpErrorCode.InvalidParams)
        };

        if (repos.Count == 0 || repos.Count > 10)
            throw new McpProtocolException("'repoName' accepts between 1 and 10 repositories", McpErrorCode.InvalidParams);
        if (repos.Find(r => !RepoNamePattern().IsMatch(r)) is { } bad)
            throw new McpProtocolException($"invalid repoName '{bad}' — expected owner/repo", McpErrorCode.InvalidParams);

        return repos;
    }
}
