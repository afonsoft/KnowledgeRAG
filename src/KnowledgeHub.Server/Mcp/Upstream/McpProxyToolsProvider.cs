using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// Generic upstream MCP proxy driven by <see cref="SourceType.McpProxy"/>
/// sources (SPEC-20260917-mcp-proxy-source-type RF-001/RF-002): every active
/// source gets a lazy <see cref="IMcpProxySession"/> whose upstream
/// <c>tools/list</c> is re-exposed verbatim — names prefixed with the source's
/// <c>namePrefix</c> (default: slugified name + "_") so homonymous tools from
/// different upstreams never collide (CA-003). Sources, secrets and activation
/// changes all invalidate the aggregate catalog through the change notifier;
/// sessions are keyed by source id and rebuilt when the fingerprint
/// (endpoint|transport|prefix) changes.
/// </summary>
public sealed partial class McpProxyToolsProvider(
    IIntegrationSecretStore secrets,
    ILoggerFactory loggerFactory,
    ILogger<McpProxyToolsProvider> logger,
    IHttpClientFactory? httpClients = null) : IToolProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _sessionsGate = new(1, 1);
    private readonly Dictionary<Guid, IMcpProxySession> _sessions = [];

    /// <summary>Test seam — builds sessions without a live upstream.</summary>
    internal Func<McpProxyConfig, IMcpProxySession>? SessionFactory { get; set; }

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetService<KnowledgeHubDbContext>();
        if (db is null)
            return [];

        var sources = await db.Sources.AsNoTracking()
            .Where(s => s.SourceType == SourceType.McpProxy && s.IsActive)
            .ToListAsync(cancellationToken);

        var tools = new List<CatalogTool>();
        var activeFingerprints = new Dictionary<Guid, string>();
        foreach (var (source, config) in sources
            .Select(s => (Source: s, Config: ParseConfig(s)))
            .Where(x => x.Config is not null))
        {
            activeFingerprints[source.Id] = config!.Fingerprint;

            var session = await GetSessionAsync(config, cancellationToken);
            foreach (var proto in await session.GetToolsAsync(cancellationToken))
                tools.Add(MapTool(session, config, proto));
        }

        await EvictStaleSessionsAsync(activeFingerprints);
        return tools;
    }

    private async Task<IMcpProxySession> GetSessionAsync(McpProxyConfig config, CancellationToken cancellationToken)
    {
        await _sessionsGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(config.SourceId, out var existing)
                && existing.Fingerprint == config.Fingerprint)
                return existing;

            if (existing is not null)
                await existing.DisposeAsync();

            var session = SessionFactory?.Invoke(config)
                ?? new McpProxySession(config, secrets, loggerFactory,
                    loggerFactory.CreateLogger<McpProxySession>(),
                    httpClients is null ? null : () => httpClients.CreateClient("mcp-upstream"));
            _sessions[config.SourceId] = session;
            return session;
        }
        finally
        {
            _sessionsGate.Release();
        }
    }

    private async Task EvictStaleSessionsAsync(Dictionary<Guid, string> activeFingerprints)
    {
        await _sessionsGate.WaitAsync();
        try
        {
            foreach (var (id, session) in _sessions.ToList())
            {
                if (activeFingerprints.TryGetValue(id, out var fp) && fp == session.Fingerprint)
                    continue;
                _sessions.Remove(id);
                await session.DisposeAsync();
            }
        }
        finally
        {
            _sessionsGate.Release();
        }
    }

    private static CatalogTool MapTool(IMcpProxySession session, McpProxyConfig config, Tool proto)
    {
        var upstreamName = proto.Name;
        var schema = JsonNode.Parse(proto.InputSchema.GetRawText()) as JsonObject
            ?? new JsonObject { ["type"] = "object" };
        return new CatalogTool
        {
            Name = config.NamePrefix + upstreamName,
            Description = proto.Description ?? $"Upstream MCP tool {upstreamName} (proxied).",
            InputSchema = schema,
            // Absent upstream hint → not read-only: proxied tools may mutate
            // remote state, so the Playground keeps the write-confirm gate.
            ReadOnly = proto.Annotations?.ReadOnlyHint ?? false,
            // RF-001 (SPEC-20260926-mcp-sdk-alignment): passthrough the remaining
            // upstream annotations; proxy tools always reach the open world.
            DestructiveHint = proto.Annotations?.DestructiveHint,
            IdempotentHint = proto.Annotations?.IdempotentHint,
            OpenWorldHint = true,
            Title = proto.Annotations?.Title,
            SourceId = config.SourceId,
            Handler = (ctx, ct) => session.CallAsync(upstreamName, ctx.Arguments, ct)
        };
    }

    /// <summary>Parses a source's configuration JSON into <see cref="McpProxyConfig"/>;
    /// returns null (with a logged warning) on invalid config so one bad source
    /// never takes down the catalog.</summary>
    internal McpProxyConfig? ParseConfig(KnowledgeSource source)
    {
        try
        {
            var node = JsonNode.Parse(source.ConfigurationJson ?? "") as JsonObject;
            var endpoint = node?["endpoint"]?.GetValue<string>();
            if (node is null || string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                logger.LogWarning("mcp-proxy source {SourceId} has no valid http(s) endpoint — skipped", source.Id);
                return null;
            }

            var transport = node["transport"]?.GetValue<string>()?.ToLowerInvariant() switch
            {
                null or "" or "auto" => HttpTransportMode.AutoDetect,
                "http" => HttpTransportMode.StreamableHttp,
                "sse" => HttpTransportMode.Sse,
                var other => InvalidTransport(other, source.Id)
            };

            var prefix = node["namePrefix"]?.GetValue<string>() ?? DefaultPrefix(source.Name);
            var ttl = node["toolsCacheSeconds"]?.GetValue<int>() is > 0 and var seconds ? seconds : 300;

            return new McpProxyConfig(source.Id, endpoint, transport, prefix, ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mcp-proxy source {SourceId} configuration parse failed — skipped", source.Id);
            return null;
        }
    }

    private static HttpTransportMode InvalidTransport(string value, Guid sourceId) =>
        throw new ArgumentException($"invalid transport '{value}' for mcp-proxy source {sourceId} (expected auto|http|sse)");

    /// <summary>Slugified source name + "_" — the default namespace (CA-003).</summary>
    internal static string DefaultPrefix(string sourceName)
    {
        var slug = SlugPattern().Replace(sourceName.ToLowerInvariant(), "_").Trim('_');
        return string.IsNullOrEmpty(slug) ? "proxy_" : slug + "_";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
        _sessionsGate.Dispose();
    }
}
