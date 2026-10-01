using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Singleton backing the /api/settings/chat endpoints and the runtime
/// <see cref="IChatClient"/> resolution (SPEC-20260916-settings-chat-config
/// RF-002/RF-003). Effective-config snapshot is loaded lazily and cached;
/// <see cref="Invalidate"/> forces a reload on next access so settings edits
/// take effect without restart.
/// </summary>
public sealed class ChatSettingsService(
    IOptions<ChatProviderOptions> envOptions,
    IIntegrationSecretStore secrets,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<ChatSettingsService> logger,
    Func<HttpClient>? probeFactory = null) : IChatSettingsService
{
    /// <summary>Test-probe timeout — deliberately shorter than chat requests.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(ChatProviderOptions Options, IChatClient? Client);

    /// <summary>Retorna as options efetivas do snapshot em cache (store sobre env).</summary>
    public ChatProviderOptions GetEffectiveOptions() => Current().Options;

    /// <summary>Retorna o client do snapshot em cache, ou null quando provider=none.</summary>
    public IChatClient? GetClient() => Current().Client;

    /// <summary>Zera o snapshot em cache; o próximo acesso reconstrói a partir do store.</summary>
    public void Invalidate()
    {
        lock (_gate)
            _snapshot = null;
    }

    /// <summary>Retorna o snapshot atual, carregando do store na primeira vez após invalidação.</summary>
    private Snapshot Current()
    {
        var snap = _snapshot;
        if (snap is not null)
            return snap;
        lock (_gate)
        {
            snap ??= LoadSnapshotAsync().GetAwaiter().GetResult();
            _snapshot = snap;
            return snap;
        }
    }

    /// <summary>Carrega a linha persistida e monta options efetivas + client (store → env).</summary>
    private async Task<Snapshot> LoadSnapshotAsync()
    {
        var env = envOptions.Value;
        var row = await FindRowAsync(CancellationToken.None);

        ChatProviderOptions options;
        if (row is not null)
        {
            options = new ChatProviderOptions
            {
                Provider = "openai",
                Endpoint = row.Endpoint,
                Model = row.Model,
                ApiKey = await secrets.GetAsync(IntegrationProviders.Chat) ?? env.ApiKey,
                Temperature = env.Temperature,
                MaxTokens = env.MaxTokens,
                TimeoutSeconds = env.TimeoutSeconds
            };
        }
        else
        {
            options = env;
        }

        IChatClient? client = null;
        try
        {
            client = ChatClientFactory.Create(options, httpFactory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "chat client build failed for provider {Provider} — treating as unconfigured", options.Provider);
        }
        return new Snapshot(options, client);
    }

    /// <summary>Monta o DTO do estado efetivo para a UI: provider, endpoint, model,
    /// origem (store|env|none) e hint mascarado da key — sem expor o segredo.</summary>
    public async Task<ChatSettingsDto> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var env = envOptions.Value;
        var envConfigured = !string.IsNullOrWhiteSpace(env.Provider)
            && !env.Provider.Equals("none", StringComparison.OrdinalIgnoreCase);
        var row = await FindRowAsync(cancellationToken);

        var (hasKey, last4, keySource) = await secrets.GetKeyStatusAsync(IntegrationProviders.Chat, env.ApiKey, cancellationToken);
        var hint = last4 is null ? null : $"••••{last4}";

        if (row is not null)
            return new ChatSettingsDto
            {
                Provider = "openai",
                Endpoint = row.Endpoint,
                Model = row.Model,
                HasApiKey = hasKey,
                ApiKeyHint = hint,
                ApiKeySource = keySource,
                Source = "store",
                EnvConfigured = envConfigured,
                UpdatedAt = row.UpdatedAt
            };

        if (envConfigured)
            return new ChatSettingsDto
            {
                Provider = env.Provider,
                Endpoint = env.Endpoint,
                Model = env.Model,
                HasApiKey = hasKey,
                ApiKeyHint = hint,
                ApiKeySource = keySource,
                Source = "env",
                EnvConfigured = true
            };

        return new ChatSettingsDto
        {
            Provider = "none",
            HasApiKey = hasKey,
            ApiKeyHint = hint,
            ApiKeySource = keySource,
            Source = "none",
            EnvConfigured = false
        };
    }

    /// <summary>Faz upsert da linha única com endpoint/model e, se a key vier
    /// preenchida, grava no store criptografado; em branco mantém a atual.
    /// Invalida o cache ao final para valer sem restart.</summary>
    public async Task SaveAsync(string endpoint, string model, string? apiKey, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var row = await db.ChatSettings.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new ChatSettings { Id = 1, Endpoint = endpoint, Model = model };
            db.ChatSettings.Add(row);
        }
        else
        {
            row.Endpoint = endpoint;
            row.Model = model;
        }
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(apiKey))
            await secrets.SetAsync(IntegrationProviders.Chat, apiKey.Trim(), cancellationToken);

        Invalidate();
        logger.LogInformation("chat settings saved (endpoint {Endpoint}, model {Model}, key {KeyAction})",
            endpoint, model, string.IsNullOrWhiteSpace(apiKey) ? "kept" : "updated");
    }

    /// <summary>Remove a key "chat" do store e invalida o cache — a key de env volta a valer.</summary>
    public async Task RemoveKeyAsync(CancellationToken cancellationToken = default)
    {
        await secrets.RemoveAsync(IntegrationProviders.Chat, cancellationToken);
        Invalidate();
        logger.LogInformation("chat API key removed from store");
    }

    /// <summary>Apaga a linha ChatSettings e a key do store, invalidando o cache —
    /// a configuração efetiva volta a ser 100% a do ambiente.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        await db.ChatSettings.ExecuteDeleteAsync(cancellationToken);
        await secrets.RemoveAsync(IntegrationProviders.Chat, cancellationToken);
        Invalidate();
        logger.LogInformation("chat settings cleared — falling back to env/config");
    }

    /// <summary>Testa a conexão chamando GET {endpoint}/v1/models com a key resolvível
    /// (request → store → env), medindo latência e conferindo se o model consta na
    /// lista retornada. Campos vazios do request usam a config efetiva. Não persiste nada.</summary>
    public async Task<TestChatConnectionResponse> TestAsync(
        TestChatConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var effective = Current().Options;
        var endpoint = !string.IsNullOrWhiteSpace(request.Endpoint) ? request.Endpoint.Trim() : effective.Endpoint;
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim() : effective.Model;
        var apiKey = !string.IsNullOrWhiteSpace(request.ApiKey) ? request.ApiKey.Trim() : effective.ApiKey;

        if (string.IsNullOrWhiteSpace(endpoint))
            return new TestChatConnectionResponse { Ok = false, LatencyMs = 0, Detail = "endpoint is required" };

        var http = probeFactory?.Invoke() ?? httpFactory.CreateClient("chat");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/v1/models");
            if (!string.IsNullOrEmpty(apiKey))
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await http.SendAsync(probe, timeout.Token);
            var latency = sw.ElapsedMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("chat connection test failed: HTTP {Status} in {LatencyMs} ms",
                    (int)response.StatusCode, latency);
                return new TestChatConnectionResponse
                {
                    Ok = false,
                    LatencyMs = latency,
                    Detail = $"HTTP {(int)response.StatusCode}"
                };
            }

            var modelListed = await CheckModelListedAsync(response, model, timeout.Token);
            logger.LogInformation("chat connection test ok in {LatencyMs} ms (modelListed={ModelListed})",
                latency, modelListed);
            return new TestChatConnectionResponse { Ok = true, LatencyMs = latency, ModelListed = modelListed };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TestChatConnectionResponse { Ok = false, LatencyMs = sw.ElapsedMilliseconds, Detail = "timeout" };
        }
        catch (HttpRequestException)
        {
            return new TestChatConnectionResponse { Ok = false, LatencyMs = sw.ElapsedMilliseconds, Detail = "connection failed" };
        }
    }

    /// <summary>Retorna true/false quando o corpo do probe é uma lista de modelos
    /// estilo OpenAI e um model foi informado; null quando não dá para avaliar.</summary>
    private static async Task<bool?> CheckModelListedAsync(
        HttpResponseMessage response, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            return data.EnumerateArray().Any(m =>
                m.TryGetProperty("id", out var id) && id.GetString() == model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lê a linha única de ChatSettings em um scope EF próprio (sem tracking).</summary>
    private async Task<Domain.Entities.ChatSettings?> FindRowAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        return await db.ChatSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }
}
