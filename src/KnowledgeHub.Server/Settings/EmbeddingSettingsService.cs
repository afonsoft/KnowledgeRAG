using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// SPEC-20260926-settings-ux-embeddings RF-004: singleton backing
/// /api/settings/embeddings and the runtime embedding provider/chunking
/// resolution (blueprint: <see cref="ChatSettingsService"/>). The effective
/// snapshot is loaded lazily, cached and rebuilt after <see cref="Invalidate"/>.
/// </summary>
public sealed class EmbeddingSettingsService(
    IOptions<EmbeddingOptions> envOptions,
    IConfiguration configuration,
    IIntegrationSecretStore secrets,
    IServiceScopeFactory scopeFactory,
    ILogger<EmbeddingSettingsService> logger,
    Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache = null,
    Caching.ICacheInvalidationBus? bus = null) : IEmbeddingSettingsService
{
    public const int DefaultMaxTokens = 500;
    public const int DefaultOverlapTokens = 50;

    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(EmbeddingOptions Options, int MaxTokens, int OverlapTokens);

    /// <inheritdoc />
    public EmbeddingOptions GetEffectiveOptions() => Current().Options;

    /// <inheritdoc />
    public (int MaxTokens, int OverlapTokens) GetChunking() =>
        (Current().MaxTokens, Current().OverlapTokens);

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
            _snapshot = null;
    }

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

    /// <summary>Store row over env: base fields from the row, advanced knobs
    /// (asymmetric prefixes, input_type) stay env-driven; key from the
    /// "embeddings" secret with env fallback.</summary>
    private async Task<Snapshot> LoadSnapshotAsync()
    {
        var env = envOptions.Value;
        var row = await FindRowAsync(CancellationToken.None);

        EmbeddingOptions options;
        if (row is not null)
        {
            options = new EmbeddingOptions
            {
                Provider = row.Provider,
                Endpoint = row.Endpoint,
                Model = row.Model,
                Dimensions = row.Dimensions,
                ModelPath = row.ModelPath,
                ApiKey = await secrets.GetAsync(IntegrationProviders.Embeddings) ?? env.ApiKey,
                Asymmetric = env.Asymmetric,
                QueryInputType = env.QueryInputType,
                DocumentInputType = env.DocumentInputType
            };
        }
        else
        {
            options = env;
        }

        var maxTokens = row?.MaxTokens
            ?? configuration.GetValue("Ingestion:MaxTokens", DefaultMaxTokens);
        var overlapTokens = row?.OverlapTokens
            ?? configuration.GetValue("Ingestion:OverlapTokens", DefaultOverlapTokens);

        return new Snapshot(options, maxTokens, overlapTokens);
    }

    /// <inheritdoc />
    public async Task<EmbeddingSettingsDto> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var env = envOptions.Value;
        var row = await FindRowAsync(cancellationToken);
        var (maxTokens, overlapTokens) = GetChunking();

        var info = await secrets.GetInfoAsync(IntegrationProviders.Embeddings, cancellationToken);
        bool hasKey;
        string? hint;
        string keySource;
        if (info is not null)
        {
            hasKey = true;
            hint = $"••••{info.KeyHint}";
            keySource = "store";
        }
        else if (!string.IsNullOrWhiteSpace(env.ApiKey))
        {
            hasKey = true;
            hint = $"••••{(env.ApiKey.Length >= 4 ? env.ApiKey[^4..] : env.ApiKey)}";
            keySource = "env";
        }
        else
        {
            hasKey = false;
            hint = null;
            keySource = "none";
        }

        return new EmbeddingSettingsDto
        {
            Provider = row?.Provider ?? env.Provider,
            Endpoint = row?.Endpoint ?? env.Endpoint,
            Model = row?.Model ?? env.Model,
            Dimensions = row?.Dimensions ?? env.Dimensions,
            ModelPath = row?.ModelPath ?? env.ModelPath,
            MaxTokens = maxTokens,
            OverlapTokens = overlapTokens,
            HasApiKey = hasKey,
            ApiKeyHint = hint,
            ApiKeySource = keySource,
            Source = row is not null ? "store" : "env",
            InstanceScoped = true,
            UpdatedAt = row?.UpdatedAt
        };
    }

    /// <inheritdoc />
    public async Task SaveAsync(SaveEmbeddingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var signatureBefore = EmbeddingProviderResolver.Signature(GetEffectiveOptions());
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var row = await db.EmbeddingSettings.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new Domain.Entities.EmbeddingSettings { Id = 1, Provider = request.Provider.Trim() };
            db.EmbeddingSettings.Add(row);
        }
        else
        {
            row.Provider = request.Provider.Trim();
        }

        row.Endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim();
        row.Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        row.ModelPath = string.IsNullOrWhiteSpace(request.ModelPath) ? null : request.ModelPath.Trim();
        row.Dimensions = request.Dimensions;
        row.MaxTokens = request.MaxTokens;
        row.OverlapTokens = request.OverlapTokens;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            await secrets.SetAsync(IntegrationProviders.Embeddings, request.ApiKey.Trim(), cancellationToken);

        Invalidate();
        logger.LogInformation(
            "embedding settings saved (provider {Provider}, model {Model}, dims {Dimensions}, key {KeyAction})",
            LogSafe(row.Provider), LogSafe(row.Model), row.Dimensions,
            string.IsNullOrWhiteSpace(request.ApiKey) ? "kept" : "updated");
        await InvalidateSearchCachesIfChangedAsync(signatureBefore, cancellationToken);
        await PublishSettingsChangedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveKeyAsync(CancellationToken cancellationToken = default)
    {
        var signatureBefore = EmbeddingProviderResolver.Signature(GetEffectiveOptions());
        await secrets.RemoveAsync(IntegrationProviders.Embeddings, cancellationToken);
        Invalidate();
        logger.LogInformation("embeddings API key removed from store");
        await InvalidateSearchCachesIfChangedAsync(signatureBefore, cancellationToken);
        await PublishSettingsChangedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var signatureBefore = EmbeddingProviderResolver.Signature(GetEffectiveOptions());
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        await db.EmbeddingSettings.ExecuteDeleteAsync(cancellationToken);
        await secrets.RemoveAsync(IntegrationProviders.Embeddings, cancellationToken);
        Invalidate();
        logger.LogInformation("embedding settings cleared — falling back to env/config");
        await InvalidateSearchCachesIfChangedAsync(signatureBefore, cancellationToken);
        await PublishSettingsChangedAsync(cancellationToken);
    }

    /// <summary>SPEC-20260926-embeddings-swap-safety RF-004: replicas cannot
    /// converge settings (each keeps its own local store), but they SHOULD at
    /// least know a change happened — publish so subscribers can invalidate
    /// their snapshots or surface a staleness hint.</summary>
    private async Task PublishSettingsChangedAsync(CancellationToken ct)
    {
        if (bus is null)
            return;
        try
        {
            await bus.PublishAsync("settings-changed", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to publish settings-changed notification");
        }
    }

    /// <summary>SPEC-20260926-embeddings-runtime-coherence RF-003: when the
    /// effective signature changed (provider/endpoint/key/dims), bump the
    /// index-version token so search/answer/tool caches are dropped locally —
    /// and publish on the bus so other replicas drop their L1 token too.</summary>
    private async Task InvalidateSearchCachesIfChangedAsync(string signatureBefore, CancellationToken ct)
    {
        if (signatureBefore == EmbeddingProviderResolver.Signature(GetEffectiveOptions()))
            return;

        if (cache is not null)
        {
            try
            {
                await Caching.SafeCache.SetStringAsync(cache, Caching.CacheKeys.IndexVersion,
                    Guid.NewGuid().ToString("N"), null, logger, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "failed to bump index version after embedding settings change");
            }
        }

        if (bus is not null)
        {
            try
            {
                await bus.PublishAsync("index-version", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "failed to publish index-version invalidation after embedding settings change");
            }
        }

        logger.LogInformation("embedding signature changed — index-version bumped, search/answer/tool caches invalidated");
    }

    /// <summary>SPEC-20260926-cache-key-consistency RF-004: strip CR/LF from
    /// user-controlled values before they reach structured logs (CodeQL
    /// cs/log-forging); truncate long inputs.</summary>
    internal static string? LogSafe(string? value)
    {
        if (value is null) return null;
        var clean = value.Replace('\n', ' ').Replace('\r', ' ');
        return clean.Length > 200 ? clean[..200] : clean;
    }

    private async Task<Domain.Entities.EmbeddingSettings?> FindRowAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        return await db.EmbeddingSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }
}
