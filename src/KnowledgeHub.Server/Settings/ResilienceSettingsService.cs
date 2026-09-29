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
/// fallback gates (SPEC-20260928-resilience-tool-fallback-wiring RF-004). Same
/// snapshot pattern as <see cref="GraphSettingsService"/>: lazy load,
/// <see cref="Invalidate"/> forces a reload so edits apply without restart.
/// </summary>
public sealed class ResilienceSettingsService(
    IOptions<FallbackOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<ResilienceSettingsService> logger) : IResilienceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private volatile FallbackOptions? _snapshot;

    /// <inheritdoc/>
    public FallbackOptions GetEffective() => Current();

    /// <inheritdoc/>
    public void Invalidate()
    {
        lock (_gate)
            _snapshot = null;
    }

    private FallbackOptions Current()
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

    /// <summary>Monta o efetivo: linha persistida → Resilience:Fallback config → defaults.</summary>
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
        // Configured capabilities are the floor — a stored row with an empty
        // map inherits the configured ones instead of silently disabling them.
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
            EnvConfigured = HasEnvConfig(),
            UpdatedAt = row?.UpdatedAt
        };
    }

    /// <inheritdoc/>
    public async Task SaveAsync(SaveResilienceSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var row = await db.ResilienceSettings.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new ResilienceSettings { Id = 1 };
            db.ResilienceSettings.Add(row);
        }

        row.Mode = request.Mode;
        row.MaxFallbackAttempts = request.MaxFallbackAttempts;
        // Masked keys ("***") mean "keep the stored value" — merge over the
        // previous row so the UI round-trips without ever exposing secrets.
        var previous = Deserialize<List<ChatProviderOptions>>(row.ChatFallbacksJson) ?? [];
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
        await db.SaveChangesAsync(cancellationToken);

        Invalidate();
        logger.LogInformation("resilience settings saved (mode {Mode}, maxAttempts {Max}, {ChatCount} chat alternates, {CapCount} capabilities)",
            row.Mode, row.MaxFallbackAttempts, fallbacks.Count, request.ToolCapabilities?.Count ?? 0);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        await db.ResilienceSettings.ExecuteDeleteAsync(cancellationToken);
        Invalidate();
        logger.LogInformation("resilience settings cleared — falling back to Resilience:Fallback config");
    }

    private bool HasEnvConfig() =>
        options.Value.Mode != "disabled"
        || options.Value.MaxFallbackAttempts != 2
        || options.Value.ChatFallbacks.Count > 0
        || options.Value.ToolCapabilities.Count > 0;

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private async Task<ResilienceSettings?> FindRowAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        return await db.ResilienceSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }
}
