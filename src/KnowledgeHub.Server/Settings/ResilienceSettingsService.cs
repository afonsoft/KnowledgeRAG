using System.Text.Json;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Resilience;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Singleton backing the /api/settings/resilience endpoints and the runtime
/// fallback gates (SPEC-20260928-resilience-tool-fallback-wiring RF-004):
/// persisted row → <c>Resilience:Fallback</c> config → defaults. Edits apply
/// without restart via snapshot invalidation.
/// </summary>
public sealed class ResilienceSettingsService(
    IOptions<FallbackOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<ResilienceSettingsService> logger)
    : SingleRowSettingsStore<ResilienceSettings>(scopeFactory), IResilienceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SnapshotCache<FallbackOptions> _cache = new();

    /// <inheritdoc/>
    public FallbackOptions GetEffective() => _cache.Get(LoadSnapshotAsync);

    /// <inheritdoc/>
    public void Invalidate() => _cache.Invalidate();

    protected override DbSet<ResilienceSettings> Set(KnowledgeHubDbContext db) =>
        db.ResilienceSettings;

    /// <summary>Efetivo: linha persistida → Resilience:Fallback config → defaults.</summary>
    private async Task<FallbackOptions> LoadSnapshotAsync()
    {
        var row = await FindRowAsync(CancellationToken.None);
        if (row is null)
            return options.Value;

        var effective = new FallbackOptions
        {
            Mode = row.Mode,
            MaxFallbackAttempts = row.MaxFallbackAttempts,
            ChatFallbacks = Deserialize<List<ChatProviderOptions>>(row.ChatFallbacksJson) ?? [],
            ToolCapabilities = Deserialize<Dictionary<string, List<string>>>(row.ToolCapabilitiesJson) ?? new()
        };
        // A stored row with an empty map inherits the configured capabilities
        // instead of silently disabling every tool fallback chain.
        if (effective.ToolCapabilities.Count == 0)
            effective.ToolCapabilities = options.Value.ToolCapabilities;
        return effective;
    }

    /// <inheritdoc/>
    public async Task<ResilienceSettingsDto> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(cancellationToken);
        var effective = GetEffective();
        return new ResilienceSettingsDto
        {
            Mode = effective.Mode,
            MaxFallbackAttempts = effective.MaxFallbackAttempts,
            ChatFallbacks = effective.ChatFallbacks.Select(f => new ChatFallbackOptionDto
            {
                Provider = f.Provider,
                Endpoint = f.Endpoint,
                Model = f.Model,
                ApiKey = string.IsNullOrEmpty(f.ApiKey) ? null : "***"
            }).ToList(),
            ToolCapabilities = effective.ToolCapabilities
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            Source = row is null ? "env" : "store",
            EnvConfigured = options.Value.Mode != "disabled"
                || options.Value.MaxFallbackAttempts != 2
                || options.Value.ChatFallbacks.Count > 0
                || options.Value.ToolCapabilities.Count > 0,
            UpdatedAt = row?.UpdatedAt
        };
    }

    /// <inheritdoc/>
    public async Task SaveAsync(SaveResilienceSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var previous = Deserialize<List<ChatProviderOptions>>(
            (await FindRowAsync(cancellationToken))?.ChatFallbacksJson) ?? [];

        await UpsertAsync(async row =>
        {
            row.Mode = request.Mode;
            row.MaxFallbackAttempts = request.MaxFallbackAttempts;
            // Masked keys ("***"/null) mean "keep the stored value" — merge over
            // the previous row so the UI round-trips without exposing secrets.
            var fallbacks = (request.ChatFallbacks ?? []).Select((f, i) => new ChatProviderOptions
            {
                Provider = f.Provider ?? "openai",
                Endpoint = f.Endpoint,
                Model = f.Model,
                ApiKey = (f.ApiKey is "***" or null) && i < previous.Count ? previous[i].ApiKey : f.ApiKey
            }).ToList();
            row.ChatFallbacksJson = JsonSerializer.Serialize(fallbacks, JsonOptions);
            row.ToolCapabilitiesJson = request.ToolCapabilities is { Count: > 0 } caps
                ? JsonSerializer.Serialize(caps, JsonOptions)
                : null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await Task.CompletedTask;
        }, cancellationToken);

        Invalidate();
        logger.LogInformation("resilience settings saved (mode {Mode}, maxAttempts {Max})",
            request.Mode, request.MaxFallbackAttempts);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await DeleteRowAsync(cancellationToken);
        Invalidate();
        logger.LogInformation("resilience settings cleared — falling back to Resilience:Fallback config");
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
}
