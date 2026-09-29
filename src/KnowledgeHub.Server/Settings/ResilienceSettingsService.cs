using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
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
/// SPEC-20260929-resilience-fallback-hardening RF-001: alternate ApiKeys live
/// in <see cref="IIntegrationSecretStore"/> under <c>resilience:fallback:{i}</c>
/// — the persisted JSON carries only a <c>hasKey</c> flag, never the key.
/// </summary>
public sealed class ResilienceSettingsService(
    IOptions<FallbackOptions> options,
    IServiceScopeFactory scopeFactory,
    IIntegrationSecretStore secrets,
    IToolCatalogChangeNotifier catalogNotifier,
    ILogger<ResilienceSettingsService> logger)
    : SingleRowSettingsStore<ResilienceSettings>(scopeFactory), IResilienceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SnapshotCache<FallbackOptions> _cache = new();

    /// <summary>Secret-store slot for a fallback's ApiKey. Bound to the
    /// entry's identity (provider|endpoint|model), not its array index —
    /// reordering or removing alternates must never hand a key to a
    /// different provider endpoint (devin-review #398).</summary>
    internal static string SecretKey(string provider, string? endpoint, string? model)
    {
        var identity = $"{provider}|{endpoint ?? ""}|{model ?? ""}";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)))[..16];
        return $"resilience:fallback:{hash}";
    }

    /// <summary>Legacy positional slot — kept only to migrate secrets stored
    /// by the index-bound format.</summary>
    private static string LegacySecretKey(int index) => $"resilience:fallback:{index}";

    /// <inheritdoc/>
    public FallbackOptions GetEffective() => _cache.Get(LoadSnapshotAsync);

    /// <inheritdoc/>
    public void Invalidate() => _cache.Invalidate();

    protected override DbSet<ResilienceSettings> Set(KnowledgeHubDbContext db) =>
        db.ResilienceSettings;

    /// <summary>Efetivo: linha persistida → Resilience:Fallback config → defaults.
    /// Legacy rows with plaintext apiKey are migrated to the secret store.</summary>
    private async Task<FallbackOptions> LoadSnapshotAsync()
    {
        var row = await FindRowAsync(CancellationToken.None);
        if (row is null)
            return options.Value;

        var fallbacks = await ReadFallbacksAsync(row);
        var effective = new FallbackOptions
        {
            Mode = row.Mode,
            MaxFallbackAttempts = row.MaxFallbackAttempts,
            ChatFallbacks = fallbacks,
            ToolCapabilities = Deserialize<Dictionary<string, List<string>>>(row.ToolCapabilitiesJson) ?? new()
        };
        // A stored row with an empty map inherits the configured capabilities
        // instead of silently disabling every tool fallback chain.
        if (effective.ToolCapabilities.Count == 0)
            effective.ToolCapabilities = options.Value.ToolCapabilities;
        return effective;
    }

    /// <summary>Reads the persisted fallback list, hydrating ApiKey from the
    /// secret store. Rows that still carry a plaintext key are migrated
    /// (written to the store, key stripped from JSON) on first load.</summary>
    private async Task<List<ChatProviderOptions>> ReadFallbacksAsync(ResilienceSettings row)
    {
        var array = string.IsNullOrWhiteSpace(row.ChatFallbacksJson)
            ? null
            : JsonNode.Parse(row.ChatFallbacksJson) as JsonArray;
        if (array is null)
            return [];

        var result = new List<ChatProviderOptions>(array.Count);
        var migrated = await MigrateStoredSecretsAsync(array, CancellationToken.None);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject f)
                continue;
            var option = new ChatProviderOptions
            {
                Provider = f["provider"]?.GetValue<string>() ?? "openai",
                Endpoint = f["endpoint"]?.GetValue<string>(),
                Model = f["model"]?.GetValue<string>()
            };
            if (f["hasKey"]?.GetValue<bool>() == true)
                option.ApiKey = await secrets.GetAsync(
                    SecretKey(option.Provider, option.Endpoint, option.Model),
                    CancellationToken.None);
            result.Add(option);
        }

        if (migrated)
        {
            // Compare-and-swap: only strip the plaintext fields if the row is
            // still the version we parsed — a concurrent SaveAsync must not be
            // overwritten by this stale write-back.
            var scrubbedJson = array.ToJsonString();
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var current = await db.ResilienceSettings.FirstOrDefaultAsync(r => r.Id == row.Id);
            if (current is not null && current.ChatFallbacksJson == row.ChatFallbacksJson)
            {
                current.ChatFallbacksJson = scrubbedJson;
                current.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                logger.LogInformation("resilience fallbacks migrated — apiKeys moved to the secret store");
            }
        }
        return result;
    }

    /// <summary>Moves legacy key material in <paramref name="array"/> to the
    /// identity-bound secret store: plaintext <c>apiKey</c> fields and slots
    /// under the old positional format <c>resilience:fallback:{i}</c>. Returns
    /// whether the array was scrubbed (plaintext removed). Plaintext is only
    /// stripped after the secret is verified readable — never destroys the
    /// only copy of a key.</summary>
    private async Task<bool> MigrateStoredSecretsAsync(JsonArray array, CancellationToken ct)
    {
        var scrubbed = false;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject f)
                continue;
            var provider = f["provider"]?.GetValue<string>() ?? "openai";
            var endpoint = f["endpoint"]?.GetValue<string>();
            var model = f["model"]?.GetValue<string>();
            var key = SecretKey(provider, endpoint, model);

            var inline = f["apiKey"]?.GetValue<string>();
            if (inline is { Length: > 0 } && inline != "***")
            {
                await secrets.SetAsync(key, inline, ct);
                // Verify before scrubbing — a vault write that can't be read
                // back must not cost the only copy of the key.
                if (await secrets.GetAsync(key, ct) is not null)
                {
                    f.Remove("apiKey");
                    f["hasKey"] = true;
                    scrubbed = true;
                }
                continue;
            }

            // Legacy index-bound slot → identity slot (copy, keep both: the
            // legacy slot stays until the row's JSON is rewritten).
            if (f["hasKey"]?.GetValue<bool>() == true
                && await secrets.GetAsync(key, ct) is null
                && await secrets.GetAsync(LegacySecretKey(i), ct) is { } legacy)
                await secrets.SetAsync(key, legacy, ct);
        }
        return scrubbed;
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
        // RF-002: the UI may omit provider — preserve the previous row's value
        // instead of silently rewriting Ollama alternates to "openai".
        var previousJson = (await FindRowAsync(cancellationToken))?.ChatFallbacksJson;
        var previousArray = (string.IsNullOrWhiteSpace(previousJson)
            ? null
            : JsonNode.Parse(previousJson) as JsonArray) ?? new JsonArray();

        // Devin-review #398: run the lazy migration BEFORE saving — a masked
        // "***" save on a never-read legacy row would otherwise overwrite the
        // only copy of the plaintext key while the vault is still empty.
        await MigrateStoredSecretsAsync(previousArray, cancellationToken);

        var previous = previousArray
            .OfType<JsonObject>()
            .Select(f => f["provider"]?.GetValue<string>())
            .ToList();

        // RF-001: keys move to the secret store before the row is written.
        var stored = new JsonArray();
        var fallbacks = request.ChatFallbacks ?? [];
        for (var i = 0; i < fallbacks.Count; i++)
        {
            var f = fallbacks[i];
            var provider = f.Provider ?? (i < previous.Count ? previous[i] : null) ?? "openai";
            var key = SecretKey(provider, f.Endpoint, f.Model);
            var hasKey = true;
            if (f.ApiKey is { Length: > 0 } k && k != "***")
                await secrets.SetAsync(key, k, cancellationToken);
            else if (f.ApiKey == "")
            {
                hasKey = false;
                await secrets.RemoveAsync(key, cancellationToken);
            }
            else // "***" or null → keep the stored secret
                hasKey = await secrets.GetAsync(key, cancellationToken) is not null;

            stored.Add(new JsonObject
            {
                ["provider"] = provider,
                ["endpoint"] = f.Endpoint,
                ["model"] = f.Model,
                ["hasKey"] = hasKey
            });
        }

        await UpsertAsync(row =>
        {
            row.Mode = request.Mode;
            row.MaxFallbackAttempts = request.MaxFallbackAttempts;
            row.ChatFallbacksJson = stored.ToJsonString();
            row.ToolCapabilitiesJson = request.ToolCapabilities is { Count: > 0 } caps
                ? JsonSerializer.Serialize(caps, JsonOptions)
                : null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }, cancellationToken);

        Invalidate();
        // RF-005: tool-fallback chains changed — the catalog's cached wrapped
        // list must rebuild so the new policy applies without a restart.
        await catalogNotifier.NotifyToolsChangedAsync(cancellationToken);
        logger.LogInformation("resilience settings saved (mode {Mode}, maxAttempts {Max})",
            ForLog(request.Mode), request.MaxFallbackAttempts);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await DeleteRowAsync(cancellationToken);
        Invalidate();
        await catalogNotifier.NotifyToolsChangedAsync(cancellationToken);
        logger.LogInformation("resilience settings cleared — falling back to Resilience:Fallback config");
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

    /// <summary>Strip CR/LF before logging caller-supplied text (CodeQL
    /// cs/log-forging) — prevents forged log lines.</summary>
    private static string ForLog(string? value) =>
        (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
}
