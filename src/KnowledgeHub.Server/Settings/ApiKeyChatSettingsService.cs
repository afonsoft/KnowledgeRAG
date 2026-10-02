using System.Collections.Concurrent;
using System.Net.Http.Headers;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Per-API-key settings service — supports chat (endpoint+model) and integration
/// API keys (firecrawl, deepwiki, tavily, context7) with fallback to global defaults
/// (SPEC-20260916-api-key-settings RF-002 expanded).
/// </summary>
public sealed class ApiKeyChatSettingsService(
    IChatSettingsService globalSettings,
    IIntegrationSecretStore secrets,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<ApiKeyChatSettingsService> logger) : IApiKeyChatSettingsService
{
    private readonly ConcurrentDictionary<Guid, Snapshot> _cache = new();

    private sealed record Snapshot(ChatProviderOptions Options, IChatClient? Client);

    public ChatProviderOptions GetEffectiveOptions(Guid apiKeyId)
        => Current(apiKeyId).Options;

    public IChatClient? GetClient(Guid apiKeyId)
        => Current(apiKeyId).Client;

    public void Invalidate(Guid apiKeyId)
        => _cache.TryRemove(apiKeyId, out _);

    public async Task<ApiKeyChatSettingsDto> DescribeAsync(Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();

        var global = globalSettings.DescribeAsync(cancellationToken);
        var row = await db.ApiKeyChatSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ApiKeyId == apiKeyId, cancellationToken);

        var globalDto = await global;
        var chatSecret = await secrets.GetInfoAsync($"apikey-chat-{apiKeyId:N}", cancellationToken);
        var firecrawlSecret = await secrets.GetInfoAsync($"apikey-firecrawl-{apiKeyId:N}", cancellationToken);
        var deepwikiSecret = await secrets.GetInfoAsync($"apikey-deepwiki-{apiKeyId:N}", cancellationToken);
        var tavilySecret = await secrets.GetInfoAsync($"apikey-tavily-{apiKeyId:N}", cancellationToken);
        var context7Secret = await secrets.GetInfoAsync($"apikey-context7-{apiKeyId:N}", cancellationToken);

        var hasOverride = row is not null && (row.Endpoint is not null || row.Model is not null);
        var overrideFields = new List<string>();
        if (row?.Endpoint is not null) overrideFields.Add("endpoint");
        if (row?.Model is not null) overrideFields.Add("model");
        if (chatSecret is not null) overrideFields.Add("apiKey");

        var effectiveEndpoint = row?.Endpoint ?? globalDto.Endpoint;
        var effectiveModel = row?.Model ?? globalDto.Model;
        var effectiveSource = hasOverride ? "apikey" : globalDto.Source;
        var effectiveKeySource = chatSecret is not null ? "apikey" : globalDto.ApiKeySource;
        var effectiveHasKey = chatSecret is not null || globalDto.HasApiKey;
        var effectiveHint = chatSecret?.KeyHint ?? globalDto.ApiKeyHint;

        return new ApiKeyChatSettingsDto
        {
            Provider = globalDto.Provider,
            Endpoint = effectiveEndpoint,
            Model = effectiveModel,
            HasApiKey = effectiveHasKey,
            ApiKeyHint = effectiveHint,
            ApiKeySource = effectiveKeySource,
            Source = effectiveSource,
            EnvConfigured = globalDto.EnvConfigured,
            UpdatedAt = row?.UpdatedAt,
            HasOverride = hasOverride || chatSecret is not null,
            OverrideFields = overrideFields.ToArray(),
            IntegrationKeys = new Dictionary<string, ApiKeyIntegrationKeyDto>
            {
                ["firecrawl"] = new(firecrawlSecret?.KeyHint, firecrawlSecret is not null),
                ["deepwiki"] = new(deepwikiSecret?.KeyHint, deepwikiSecret is not null),
                ["tavily"] = new(tavilySecret?.KeyHint, tavilySecret is not null),
                ["context7"] = new(context7Secret?.KeyHint, context7Secret is not null)
            }
        };
    }

    public async Task SaveAsync(Guid apiKeyId, string? endpoint, string? model, string? apiKey, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();

        var row = await db.ApiKeyChatSettings
            .FirstOrDefaultAsync(s => s.ApiKeyId == apiKeyId, cancellationToken);

        if (endpoint is null && model is null && apiKey is null)
            await ClearSettingsAsync(db, row, apiKeyId, cancellationToken);
        else
            await ApplySettingsAsync(db, row, apiKeyId, new ChatSettingsPatch(endpoint, model, apiKey), cancellationToken);

        Invalidate(apiKeyId);
    }

    private sealed record ChatSettingsPatch(string? Endpoint, string? Model, string? ApiKey);

    private async Task ClearSettingsAsync(
        KnowledgeHubDbContext db, ApiKeyChatSettings? row, Guid apiKeyId, CancellationToken cancellationToken)
    {
        if (row is not null)
        {
            db.ApiKeyChatSettings.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
        await secrets.RemoveAsync($"apikey-chat-{apiKeyId:N}", cancellationToken);
    }

    private async Task ApplySettingsAsync(
        KnowledgeHubDbContext db, ApiKeyChatSettings? row, Guid apiKeyId,
        ChatSettingsPatch patch, CancellationToken cancellationToken)
    {
        await UpsertSettingsRowAsync(db, row, apiKeyId, patch, cancellationToken);

        if (patch.ApiKey is not null && !string.IsNullOrWhiteSpace(patch.ApiKey))
        {
            await secrets.SetAsync($"apikey-chat-{apiKeyId:N}", patch.ApiKey.Trim(), cancellationToken);
        }
    }

    private static async Task UpsertSettingsRowAsync(
        KnowledgeHubDbContext db, ApiKeyChatSettings? row, Guid apiKeyId,
        ChatSettingsPatch patch, CancellationToken cancellationToken)
    {
        if (row is null && (patch.Endpoint is not null || patch.Model is not null))
        {
            row = new ApiKeyChatSettings { ApiKeyId = apiKeyId };
            db.ApiKeyChatSettings.Add(row);
        }

        if (row is null)
            return;

        ApplyPatch(row, patch);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyPatch(ApiKeyChatSettings row, ChatSettingsPatch patch)
    {
        if (patch.Endpoint is not null)
            row.Endpoint = string.IsNullOrWhiteSpace(patch.Endpoint) ? null : patch.Endpoint.Trim();
        if (patch.Model is not null)
            row.Model = string.IsNullOrWhiteSpace(patch.Model) ? null : patch.Model.Trim();
    }

    public async Task SaveIntegrationKeyAsync(Guid apiKeyId, string provider, string apiKey, CancellationToken cancellationToken = default)
    {
        await secrets.SetAsync($"apikey-{provider}-{apiKeyId:N}", apiKey.Trim(), cancellationToken);
    }

    public async Task RemoveIntegrationKeyAsync(Guid apiKeyId, string provider, CancellationToken cancellationToken = default)
    {
        await secrets.RemoveAsync($"apikey-{provider}-{apiKeyId:N}", cancellationToken);
    }

    public async Task RemoveAsync(Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();

        var row = await db.ApiKeyChatSettings
            .FirstOrDefaultAsync(s => s.ApiKeyId == apiKeyId, cancellationToken);

        if (row is not null)
        {
            db.ApiKeyChatSettings.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        await secrets.RemoveAsync($"apikey-chat-{apiKeyId:N}", cancellationToken);
        await secrets.RemoveAsync($"apikey-firecrawl-{apiKeyId:N}", cancellationToken);
        await secrets.RemoveAsync($"apikey-deepwiki-{apiKeyId:N}", cancellationToken);
        await secrets.RemoveAsync($"apikey-tavily-{apiKeyId:N}", cancellationToken);
        await secrets.RemoveAsync($"apikey-context7-{apiKeyId:N}", cancellationToken);
        Invalidate(apiKeyId);
    }

    public async Task<string?> GetIntegrationSecretAsync(Guid apiKeyId, string provider, CancellationToken cancellationToken = default)
    {
        var perKey = await secrets.GetAsync($"apikey-{provider}-{apiKeyId:N}", cancellationToken);
        if (perKey is not null) return perKey;
        return await secrets.GetAsync(provider, cancellationToken);
    }

    private Snapshot Current(Guid apiKeyId)
    {
        if (_cache.TryGetValue(apiKeyId, out var cached))
            return cached;

        var options = BuildOptions(apiKeyId);
        var client = BuildClient(options);
        var snapshot = new Snapshot(options, client);
        _cache[apiKeyId] = snapshot;
        return snapshot;
    }

    private ChatProviderOptions BuildOptions(Guid apiKeyId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();

        var row = db.ApiKeyChatSettings
            .AsNoTracking()
            .FirstOrDefault(s => s.ApiKeyId == apiKeyId);

        var globalOpts = globalSettings.GetEffectiveOptions();

        var endpoint = row?.Endpoint ?? globalOpts.Endpoint;
        var model = row?.Model ?? globalOpts.Model;

        string? apiKey = null;
        var secret = secrets.GetAsync($"apikey-chat-{apiKeyId:N}").GetAwaiter().GetResult();
        if (secret is not null)
            apiKey = secret;
        else
            apiKey = globalOpts.ApiKey;

        return new ChatProviderOptions
        {
            Provider = globalOpts.Provider,
            Endpoint = endpoint,
            Model = model,
            ApiKey = apiKey
        };
    }

    private IChatClient? BuildClient(ChatProviderOptions options)
    {
        if (options.Provider == "none" || string.IsNullOrWhiteSpace(options.Endpoint))
            return null;

        try
        {
            var http = httpFactory.CreateClient();
            http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/'));
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
            http.Timeout = TimeSpan.FromMinutes(5);

            return options.Provider switch
            {
                "ollama" => new OllamaChatClient(http, options),
                _ => new OpenAiChatClient(http, options)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build chat client for API key {ApiKeyId}", options);
            return null;
        }
    }
}
