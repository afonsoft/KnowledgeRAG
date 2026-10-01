using System.Text.Json;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// Lazy singleton <see cref="McpClient"/> to DeepWiki (SPEC-07 RF-001).
/// AutoDetect transport (Streamable HTTP, SSE fallback), one reconnect attempt
/// on transport/session failure. SPEC-20260916 RF-008: the effective key
/// resolves from the integration secret store (DB) then env/config; when a key
/// exists the client targets <see cref="DeepWikiOptions.PrivateEndpoint"/>
/// (mcp.devin.ai) with Bearer auth, otherwise the public endpoint.
/// </summary>
public sealed class DeepWikiUpstreamClient(
    IOptions<DeepWikiOptions> options,
    IIntegrationSecretStore secrets,
    ILoggerFactory loggerFactory,
    ILogger<DeepWikiUpstreamClient> logger) : IAsyncDisposable
{
    private readonly DeepWikiOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private string? _connectedKey;

    /// <summary>Test seam — replaces transport construction.</summary>
    internal Func<HttpClientTransportOptions, IClientTransport>? TransportFactory { get; set; }

    /// <summary>Effective API key (store → env/config). Null when none is configured.</summary>
    public async Task<string?> ResolveApiKeyAsync(CancellationToken cancellationToken = default) =>
        await secrets.GetEffectiveAsync(IntegrationProviders.DeepWiki, _options.ApiKey, cancellationToken);

    public async Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default) =>
        await ResolveApiKeyAsync(cancellationToken) is not null;

    /// <summary>Endpoint in effect for a given key state — private when keyed, public otherwise.</summary>
    public Uri EffectiveEndpoint(string? apiKey) =>
        new(!string.IsNullOrEmpty(apiKey) ? _options.PrivateEndpoint : _options.Endpoint);

    /// <summary>Invoke an upstream tool; never throws — failures become IsError results.
    /// <paramref name="apiKeyOverride"/> is the caller's per-key effective secret
    /// (SPEC-20260922-per-key-integration-secrets RF-002/RF-003) — when set it
    /// wins over the store/env resolution (and switches to the private endpoint
    /// via <see cref="EffectiveEndpoint"/>) and drives a reconnect via
    /// <c>_connectedKey</c>.</summary>
    public async Task<CallToolResult> CallAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken,
        string? apiKeyOverride = null)
    {
        var dict = arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        try
        {
            return await InvokeAsync(toolName, dict, cancellationToken, apiKeyOverride);
        }
        catch (Exception first) when (first is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(first, "DeepWiki call failed; reconnecting once");
            await ResetAsync();
            try
            {
                return await InvokeAsync(toolName, dict, cancellationToken, apiKeyOverride);
            }
            catch (Exception second)
            {
                return ErrorResult($"deepwiki upstream error: {second.Message}");
            }
        }
    }

    private async Task<CallToolResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken,
        string? apiKeyOverride)
    {
        var client = await GetClientAsync(cancellationToken, apiKeyOverride);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token);
        // Some upstreams serialize "isError":null instead of omitting it — MCP
        // treats absent as success, so normalize to keep the contract boolean.
        result.IsError ??= false;
        return result;
    }

    /// <summary>Upstream tools/list — verbatim names + schemas for the dynamic
    /// merge in private mode (SPEC-20260917-upstream-tools-passthrough RF-001).
    /// Throws on transport failure; callers apply cache/fallback.</summary>
    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        return await client.ListToolsAsync(cancellationToken: timeout.Token);
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken, string? apiKeyOverride = null)
    {
        var apiKey = apiKeyOverride ?? await ResolveApiKeyAsync(cancellationToken);

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

            var transportOptions = CreateTransportOptions(apiKey);

            var transport = TransportFactory?.Invoke(transportOptions)
                ?? new HttpClientTransport(transportOptions, loggerFactory);

            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "knowledge-hub-deepwiki-proxy", Version = "0.1.0" }
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

    /// <summary>Transport options for the given key state — private endpoint +
    /// Bearer when keyed, public endpoint without auth otherwise (test seam).</summary>
    internal HttpClientTransportOptions CreateTransportOptions(string? apiKey)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = EffectiveEndpoint(apiKey),
            TransportMode = HttpTransportMode.AutoDetect
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            transportOptions.AdditionalHeaders ??= new Dictionary<string, string>();
            transportOptions.AdditionalHeaders["Authorization"] = $"Bearer {apiKey}";
        }
        return transportOptions;
    }

    /// <summary>Drops the current session so the next call reconnects with the
    /// current effective key/endpoint (Settings save/remove hook).</summary>
    public async Task ResetAsync()
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

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _gate.Dispose();
    }
}
