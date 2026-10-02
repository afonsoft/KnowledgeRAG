using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using A2A;
using KnowledgeHub.Server.Assistant;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Singleton backing /api/settings/assistant and invalidating the
/// <see cref="IAssistantChatClientProvider"/> snapshot (SPEC-20260929-a2a-
/// assistant-delegation RF-001). Store row overrides env; the API key lives in
/// the secret store under <see cref="IntegrationProviders.Assistant"/>.
/// </summary>
public sealed class AssistantSettingsService(
    IOptions<AssistantOptions> envOptions,
    IIntegrationSecretStore secrets,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    IAssistantChatClientProvider provider,
    IChatSettingsService chatSettings,
    ILogger<AssistantSettingsService> logger)
    : SingleRowSettingsStore<AssistantSettings>(scopeFactory), IAssistantSettingsService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] KnownSubtasks = ["rewrite", "grade", "expand", "summarize"];

    protected override DbSet<AssistantSettings> Set(KnowledgeHubDbContext db) => db.AssistantSettings;

    public async Task<AssistantSettingsDto> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var env = envOptions.Value;
        var row = await FindRowAsync(cancellationToken);
        var info = await secrets.GetInfoAsync(IntegrationProviders.Assistant, cancellationToken);
        bool hasKey;
        string? hint;
        string keySource;
        if (info is not null)
        {
            (hasKey, hint, keySource) = (true, $"••••{info.KeyHint}", "store");
        }
        else if (!string.IsNullOrWhiteSpace(env.ApiKey))
        {
            var k = env.ApiKey;
            (hasKey, hint, keySource) = (true, $"••••{(k.Length >= 4 ? k[^4..] : k)}", "env");
        }
        else
        {
            (hasKey, hint, keySource) = (false, null, "none");
        }

        if (row is not null)
            return new AssistantSettingsDto
            {
                Enabled = row.Enabled,
                Mode = row.Mode,
                Endpoint = row.Endpoint,
                Model = row.Model,
                Route = ParseRoute(row.RouteJson) ?? env.Route,
                TimeoutSeconds = row.TimeoutSeconds,
                HasApiKey = hasKey,
                ApiKeyHint = hint,
                ApiKeySource = keySource,
                Source = "store",
                UpdatedAt = row.UpdatedAt
            };

        return new AssistantSettingsDto
        {
            Enabled = env.Enabled,
            Mode = env.Mode,
            Endpoint = env.Endpoint,
            Model = env.Model,
            Route = env.Route,
            TimeoutSeconds = env.TimeoutSeconds,
            HasApiKey = hasKey,
            ApiKeyHint = hint,
            ApiKeySource = keySource,
            Source = env.Enabled ? "env" : "none"
        };
    }

    public async Task SaveAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var mode = request.Mode.Equals("remote", StringComparison.OrdinalIgnoreCase) ? "remote" : "local";
        var route = request.Route is null
            ? null
            : JsonSerializer.Serialize(request.Route
                .Where(r => KnownSubtasks.Contains(r, StringComparer.OrdinalIgnoreCase))
                .Select(r => r.ToLowerInvariant())
                .Distinct()
                .ToArray());
        var timeout = Math.Clamp(request.TimeoutSeconds ?? 15, 1, 300);

        await UpsertAsync(async row =>
        {
            row.Enabled = request.Enabled;
            row.Mode = mode;
            row.Endpoint = request.Endpoint?.Trim() ?? "";
            row.Model = request.Model?.Trim();
            row.RouteJson = route;
            row.TimeoutSeconds = timeout;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await Task.CompletedTask;
        }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            await secrets.SetAsync(IntegrationProviders.Assistant, request.ApiKey.Trim(), cancellationToken);

        provider.Invalidate();
        logger.LogInformation("assistant settings saved (mode {Mode}, enabled {Enabled}, key {KeyAction})",
            mode, request.Enabled, string.IsNullOrWhiteSpace(request.ApiKey) ? "kept" : "updated");
    }

    public async Task RemoveKeyAsync(CancellationToken cancellationToken = default)
    {
        await secrets.RemoveAsync(IntegrationProviders.Assistant, cancellationToken);
        provider.Invalidate();
        logger.LogInformation("assistant API key removed from store");
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await DeleteRowAsync(cancellationToken);
        await secrets.RemoveAsync(IntegrationProviders.Assistant, cancellationToken);
        provider.Invalidate();
        logger.LogInformation("assistant settings cleared — falling back to env/config");
    }

    /// <summary>Tests connectivity: local mode probes GET {endpoint}/v1/models;
    /// remote mode resolves the A2A Agent Card.</summary>
    public async Task<TestChatConnectionResponse> TestAsync(
        TestAssistantConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var dto = await DescribeAsync(cancellationToken);
        var mode = !string.IsNullOrWhiteSpace(request.Mode) ? request.Mode : dto.Mode;
        var endpoint = !string.IsNullOrWhiteSpace(request.Endpoint) ? request.Endpoint.Trim() : dto.Endpoint;
        var apiKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : await ResolveApiKeyAsync(endpoint, cancellationToken);

        if (string.IsNullOrWhiteSpace(endpoint))
            return new TestChatConnectionResponse { Ok = false, LatencyMs = 0, Detail = "endpoint is required" };

        var http = httpFactory.CreateClient("chat");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            if (mode.Equals("remote", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var resolver = new A2ACardResolver(new Uri(endpoint), http,
                        "/.well-known/agent-card.json", logger);
                    var card = await resolver.GetAgentCardAsync(timeout.Token);
                    var ok = card.SupportedInterfaces.Count > 0;
                    return new TestChatConnectionResponse
                    {
                        Ok = ok,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Detail = ok ? $"agent '{card.Name}' — {card.SupportedInterfaces.Count} interface(s)" : "agent card has no interfaces"
                    };
                }
                catch (global::A2A.A2AException)
                {
                    return new TestChatConnectionResponse
                    {
                        Ok = false,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Detail = "invalid agent card"
                    };
                }
            }

            using var probe = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/v1/models");
            if (!string.IsNullOrEmpty(apiKey))
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(probe, timeout.Token);
            return new TestChatConnectionResponse
            {
                Ok = response.IsSuccessStatusCode,
                LatencyMs = sw.ElapsedMilliseconds,
                Detail = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TestChatConnectionResponse { Ok = false, LatencyMs = sw.ElapsedMilliseconds, Detail = "timeout" };
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
        {
            return new TestChatConnectionResponse { Ok = false, LatencyMs = sw.ElapsedMilliseconds, Detail = "connection failed" };
        }
    }

    /// <summary>Lists model ids for the assistant combo (Settings → Assistente):
    /// probes GET {endpoint}/v1/models with the assistant key (store → env).
    /// Endpoint resolution mirrors <see cref="TestAsync"/> — request override,
    /// then the effective config.</summary>
    public async Task<ProviderModelsResponse> ListModelsAsync(
        string? endpointOverride, CancellationToken cancellationToken = default)
    {
        var dto = await DescribeAsync(cancellationToken);
        var endpoint = !string.IsNullOrWhiteSpace(endpointOverride) ? endpointOverride.Trim() : dto.Endpoint;
        var apiKey = await ResolveApiKeyAsync(endpoint, cancellationToken);
        var http = httpFactory.CreateClient("chat");
        return await ProviderModelProbe.ListAsync(http, endpoint, apiKey, cancellationToken);
    }

    /// <summary>Resolves the assistant key: own store → Assistant:ApiKey env.
    /// When the probed endpoint is the same as the chat provider's, the chat
    /// key applies too (same provider) — but the chat key is NEVER sent to a
    /// different endpoint.</summary>
    private async Task<string?> ResolveApiKeyAsync(string? endpoint, CancellationToken ct)
    {
        var key = await secrets.GetAsync(IntegrationProviders.Assistant, ct)
            ?? envOptions.Value.ApiKey;
        if (!string.IsNullOrEmpty(key))
            return key;
        var chat = chatSettings.GetEffectiveOptions();
        return IsSameEndpoint(endpoint, chat.Endpoint) ? chat.ApiKey : null;
    }

    internal static bool IsSameEndpoint(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string[]? ParseRoute(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
