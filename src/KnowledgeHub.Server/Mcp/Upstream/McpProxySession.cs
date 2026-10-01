using System.Text.Json;
using KnowledgeHub.Server.Settings;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>Parsed per-source proxy configuration (SPEC-20260917-mcp-proxy-source-type RF-001).</summary>
public sealed record McpProxyConfig(
    Guid SourceId,
    string Endpoint,
    HttpTransportMode Transport,
    string NamePrefix,
    int ToolsCacheSeconds = 300)
{
    /// <summary>Fingerprint used to decide when a live session must be
    /// rebuilt — endpoint/transport/prefix changes force reconnect.</summary>
    public string Fingerprint => $"{Endpoint}|{Transport}|{NamePrefix}";
}

/// <summary>Session seam so tests can drive the provider without a live
/// upstream MCP server.</summary>
public interface IMcpProxySession : IAsyncDisposable
{
    string Fingerprint { get; }
    /// <summary>Upstream tools/list with TTL cache + last-known-good fallback;
    /// empty when nothing was ever fetched.</summary>
    Task<IReadOnlyList<Tool>> GetToolsAsync(CancellationToken cancellationToken);
    /// <summary>Invoke an upstream tool; never throws — failures become IsError results.</summary>
    ValueTask<CallToolResult> CallAsync(
        string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Lazy per-source <see cref="McpClient"/> session (SPEC-20260917-mcp-proxy-source-type
/// RF-002) — same pattern as <see cref="DeepWikiUpstreamClient"/>: gated lazy
/// connect, one reconnect on transport failure, key resolved per call from the
/// encrypted store under <c>mcpproxy:{sourceId}</c> so Settings edits reconnect.
/// </summary>
internal sealed class McpProxySession(
    McpProxyConfig config,
    IIntegrationSecretStore secrets,
    ILoggerFactory loggerFactory,
    ILogger<McpProxySession> logger,
    Func<HttpClient>? httpClientFactory = null) : IMcpProxySession
{
    public const int CallTimeoutSeconds = 60;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private McpClient? _client;
    private string? _connectedKey;
    private IReadOnlyList<Tool>? _cachedTools;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public string Fingerprint => config.Fingerprint;

    /// <summary>Secret store slug for this source's API key.</summary>
    public static string SecretKey(Guid sourceId) => $"mcpproxy:{sourceId}";

    /// <summary>Test seam — replaces transport construction.</summary>
    internal Func<HttpClientTransportOptions, IClientTransport>? TransportFactory { get; set; }

    public async Task<IReadOnlyList<Tool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (_cachedTools is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            return _cachedTools;

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedTools is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
                return _cachedTools;

            try
            {
                var client = await GetClientAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(CallTimeoutSeconds));
                var listed = await client.ListToolsAsync(cancellationToken: timeout.Token);
                _cachedTools = listed.Select(t => t.ProtocolTool).ToList();
                _cacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(config.ToolsCacheSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // last-known-good wins; empty when nothing was ever fetched.
                logger.LogWarning(ex, "mcp-proxy {SourceId} tools/list failed — serving cached set", config.SourceId);
            }
            return _cachedTools ?? [];
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async ValueTask<CallToolResult> CallAsync(
        string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        var dict = arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        try
        {
            return await InvokeAsync(toolName, dict, cancellationToken);
        }
        catch (Exception first) when (first is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(first, "mcp-proxy {SourceId} call failed; reconnecting once", config.SourceId);
            await ResetAsync();
            try
            {
                return await InvokeAsync(toolName, dict, cancellationToken);
            }
            catch (Exception second)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"mcp-proxy upstream error: {second.Message}" }]
                };
            }
        }
    }

    private async Task<CallToolResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(CallTimeoutSeconds));
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token);
        result.IsError ??= false;
        return result;
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        var apiKey = await secrets.GetAsync(SecretKey(config.SourceId), cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && _connectedKey == apiKey)
                return _client;
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Endpoint),
                TransportMode = config.Transport
            };
            if (!string.IsNullOrEmpty(apiKey))
                transportOptions.AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {apiKey}"
                };

            // SPEC-20260923-agent-runtime-hardening RF-004: HTTP transports use
            // the resilient "mcp-upstream" named client (caller keeps ownership).
            var transport = TransportFactory?.Invoke(transportOptions)
                ?? (httpClientFactory?.Invoke() is { } httpClient
                    ? new HttpClientTransport(transportOptions, httpClient, loggerFactory, ownsHttpClient: false)
                    : new HttpClientTransport(transportOptions, loggerFactory));

            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = $"knowledge-hub-mcp-proxy-{config.SourceId:N}", Version = "0.1.0" }
                },
                loggerFactory,
                cancellationToken);
            _connectedKey = apiKey;
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
                _connectedKey = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _gate.Dispose();
        _cacheGate.Dispose();
    }
}
