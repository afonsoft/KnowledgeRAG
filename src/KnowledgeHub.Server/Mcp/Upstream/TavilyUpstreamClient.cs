using System.Text.Json;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// Lazy singleton <see cref="McpClient"/> to the Tavily MCP
/// (SPEC-20260916-tavily-mcp-proxy RF-002) — same shape as
/// <see cref="FirecrawlUpstreamClient"/>: AutoDetect transport (Streamable HTTP,
/// SSE fallback), Bearer auth, one reconnect attempt on transport/session
/// failure. The effective API key resolves from the integration secret store
/// (DB) first, then <see cref="TavilyOptions.ApiKey"/> (env/config). Bearer is
/// used instead of the upstream ?tavilyApiKey= query param so the key never
/// lands in URLs/access logs.
/// </summary>
public sealed class TavilyUpstreamClient(
    IOptions<TavilyOptions> options,
    IIntegrationSecretStore secrets,
    ILoggerFactory loggerFactory,
    ILogger<TavilyUpstreamClient> logger) : IAsyncDisposable
{
    private readonly TavilyOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private string? _connectedKey;

    /// <summary>Test seam — replaces transport construction.</summary>
    internal Func<HttpClientTransportOptions, IClientTransport>? TransportFactory { get; set; }

    /// <summary>Effective API key (store → env/config). Null when none is configured.</summary>
    public async Task<string?> ResolveApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var stored = await secrets.GetAsync(IntegrationProviders.Tavily, cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;
        var configured = _options.ApiKey;
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    public async Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default) =>
        await ResolveApiKeyAsync(cancellationToken) is not null;

    /// <summary>Invoke an upstream tool; never throws — failures become IsError results.
    /// <paramref name="apiKeyOverride"/> is the caller's per-key effective secret
    /// (SPEC-20260922-per-key-integration-secrets RF-002/RF-003) — when set it
    /// wins over the store/env resolution and drives a reconnect via
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
            logger.LogInformation(first, "tavily call failed; reconnecting once");
            await ResetAsync();
            try
            {
                return await InvokeAsync(toolName, dict, cancellationToken, apiKeyOverride);
            }
            catch (Exception second)
            {
                return ErrorResult($"tavily upstream error: {second.Message}");
            }
        }
    }

    /// <summary>Upstream tools/list — verbatim names + schemas for the dynamic merge
    /// (RF-003). Throws on transport failure; callers apply cache/fallback.</summary>
    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        return await client.ListToolsAsync(cancellationToken: timeout.Token);
    }

    /// <summary>Drops the current session so the next call reconnects with the
    /// current effective key (Settings save/remove hook).</summary>
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

    /// <summary>Transport options for the given key state — Bearer auth only
    /// when a key is configured (test seam).</summary>
    internal HttpClientTransportOptions CreateTransportOptions(string? apiKey)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_options.Endpoint),
            TransportMode = HttpTransportMode.AutoDetect
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            transportOptions.AdditionalHeaders ??= new Dictionary<string, string>();
            transportOptions.AdditionalHeaders["Authorization"] = $"Bearer {apiKey}";
        }
        return transportOptions;
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

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken, string? apiKeyOverride = null)
    {
        var apiKey = apiKeyOverride ?? await ResolveApiKeyAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Reconnect transparently when the effective key changed under us
            // (e.g. env reload) — ResetAsync already covers the Settings path.
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
                    ClientInfo = new Implementation { Name = "knowledge-hub-tavily-proxy", Version = "0.1.0" }
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
