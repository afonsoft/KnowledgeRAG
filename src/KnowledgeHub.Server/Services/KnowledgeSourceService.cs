using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.Upstream;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Services;

/// <summary>CRUD + validation + secret redaction for knowledge sources (SPEC-02 RF-001/RF-002).</summary>
public sealed class KnowledgeSourceService(
    KnowledgeHubDbContext db,
    IToolCatalogChangeNotifier catalogNotifier,
    IIntegrationSecretStore secrets,
    Ingestion.Staging.IStagingStorageService? staging = null,
    VectorStore.IVectorStore? vectors = null,
    ILogger<KnowledgeSourceService>? log = null,
    Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache = null,
    Caching.ICacheInvalidationBus? invalidationBus = null) : IKnowledgeSourceService
{
    /// <summary>Config keys that must never be echoed back to API consumers.</summary>
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
        { "connectionString", "apiKey", "key", "headers", "token", "password", "secret",
          "secretAccessKey", "accountKey" };

    /// <summary>Required configuration keys per connector type (SPEC-02 §Scope).</summary>
    private static readonly Dictionary<SourceType, string[]> RequiredKeys = new()
    {
        [SourceType.ObsidianVault] = ["path"],
        [SourceType.WebPage] = ["url"],
        [SourceType.RestApi] = ["endpoint"],
        [SourceType.SqlDatabase] = ["provider", "query"],
        [SourceType.DocumentFile] = ["path"],
        [SourceType.McpProxy] = ["endpoint"],
        [SourceType.Notion] = [],
        [SourceType.AwsS3] = ["bucketName", "region", "accessKeyId"],
        [SourceType.AzureFiles] = ["shareName"],
        [SourceType.OciStorage] = ["namespace", "region", "bucketName", "accessKeyId"],
        [SourceType.GoogleDrive] = ["sharedUrl"],
        [SourceType.RssFeed] = ["feedUrl"],
        [SourceType.YouTube] = ["urls"]
    };

    public async Task<IReadOnlyList<KnowledgeSourceDto>> ListAsync(SourceType? type, bool? active, CancellationToken ct = default)
    {
        var query = db.Sources.AsNoTracking();
        if (type is not null)
            query = query.Where(s => s.SourceType == type);
        if (active is not null)
            query = query.Where(s => s.IsActive == active);
        var sources = await query.OrderBy(s => s.Name).ToListAsync(ct);
        return sources.Select(ToDto).ToList();
    }

    public async Task<KnowledgeSourceDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Sources.AsNoTracking().Where(s => s.Id == id).Select(s => ToDto(s)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult<KnowledgeSourceDto>> CreateAsync(CreateKnowledgeSourceRequest request, CancellationToken ct = default)
    {
        var validation = Validate(request.Name, request.Type, request.Configuration);
        if (validation is not null)
            return ServiceResult<KnowledgeSourceDto>.Fail(400, validation);

        if (await db.Sources.AnyAsync(s => s.Name == request.Name, ct))
            return ServiceResult<KnowledgeSourceDto>.Fail(409, $"A source named '{request.Name}' already exists");

        var source = new KnowledgeSource
        {
            Name = request.Name,
            Description = request.Description,
            SourceType = request.Type,
            ConfigurationJson = request.Configuration?.ToJsonString(),
            IsActive = request.IsActive,
            AutoSyncEnabled = request.AutoSyncEnabled,
            SyncIntervalMinutes = request.SyncIntervalMinutes
        };
        var secretError = await ValidateConnectorSecretAsync(source, request.Configuration, ct);
        if (secretError is not null)
            return ServiceResult<KnowledgeSourceDto>.Fail(400, secretError);
        db.Sources.Add(source);
        await PersistProxySecretAsync(source, request.Configuration, ct);
        await db.SaveChangesAsync(ct);
        await NotifyCatalogChanged(ct);
        return ServiceResult<KnowledgeSourceDto>.Ok(ToDto(source));
    }

    public async Task<ServiceResult<KnowledgeSourceDto>> UpdateAsync(Guid id, UpdateKnowledgeSourceRequest request, CancellationToken ct = default)
    {
        var source = await db.Sources.FindAsync([id], ct);
        if (source is null)
            return ServiceResult<KnowledgeSourceDto>.Fail(404, "Source not found");

        var validation = Validate(request.Name, source.SourceType, request.Configuration);
        if (validation is not null)
            return ServiceResult<KnowledgeSourceDto>.Fail(400, validation);

        if (await db.Sources.AnyAsync(s => s.Name == request.Name && s.Id != id, ct))
            return ServiceResult<KnowledgeSourceDto>.Fail(409, $"A source named '{request.Name}' already exists");

        source.Name = request.Name;
        source.Description = request.Description;
        source.ConfigurationJson = request.Configuration?.ToJsonString();
        source.IsActive = request.IsActive;
        source.AutoSyncEnabled = request.AutoSyncEnabled;
        source.SyncIntervalMinutes = request.SyncIntervalMinutes;
        var secretError = await ValidateConnectorSecretAsync(source, request.Configuration, ct);
        if (secretError is not null)
            return ServiceResult<KnowledgeSourceDto>.Fail(400, secretError);
        await PersistProxySecretAsync(source, request.Configuration, ct);
        await db.SaveChangesAsync(ct);
        await NotifyCatalogChanged(ct);
        return ServiceResult<KnowledgeSourceDto>.Ok(ToDto(source));
    }

    public async Task<ServiceResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var source = await db.Sources.FindAsync([id], ct);
        if (source is null)
            return ServiceResult<bool>.Fail(404, "Source not found");

        // RF-008 (SPEC-20260926-ingestion-connector-integrity): commit the
        // deletion FIRST — the source row is the source of truth. Every step
        // after this is best-effort cleanup: a failure must never resurrect a
        // deleted source nor leave a live source without vectors.
        db.Sources.Remove(source); // cascade removes documents + chunks
        await db.SaveChangesAsync(ct);

        // SPEC-20260925-pgvector-source-cascade RF-002: external vector tables
        // (pgvector kh_embeddings / sqlite-vec vec_chunks) are outside the EF
        // cascade — purge explicitly. Fail-soft: rows may orphan, never block.
        if (vectors is not null)
        {
            try { await vectors.DeleteBySourceAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogWarning(ex, "vector purge for deleted source {SourceId} failed — rows may be orphaned", id);
            }
        }
        try
        {
            if (source.SourceType == SourceType.McpProxy)
                await secrets.RemoveAsync(McpProxySession.SecretKey(source.Id), ct);
            if (source.SourceType == SourceType.Notion)
                await secrets.RemoveAsync(Ingestion.Connectors.NotionConnector.SecretKey(source.Id), ct);
            // SPEC-20260927-restapi-sqldatabase-connectors RF-003/RF-006:
            // stored headers/connectionString are purged with the source.
            if (source.SourceType == SourceType.RestApi)
                await secrets.RemoveAsync(Ingestion.Connectors.RestApiConnector.SecretKey(source.Id), ct);
            if (source.SourceType == SourceType.SqlDatabase)
                await secrets.RemoveAsync(Ingestion.Connectors.SqlDatabaseConnector.SecretKey(source.Id), ct);
            // SPEC-20260924-cloud-storage-connectors RF-006: purge cloud secrets + staging.
            if (source.SourceType is SourceType.AwsS3 or SourceType.AzureFiles or SourceType.OciStorage)
            {
                foreach (var key in CloudSecretKeys(source.SourceType, source.Id))
                    await secrets.RemoveAsync(key, ct);
                if (staging is not null)
                    await staging.CleanupStagingAsync(source.Id, ct);
            }
            // SPEC-20260924-gdrive-shared-link-connector RF-006/RF-008.
            if (source.SourceType == SourceType.GoogleDrive)
            {
                await secrets.RemoveAsync(Ingestion.Connectors.GoogleDriveSharedConnector.SecretKey(source.Id), ct);
                if (staging is not null)
                    await staging.CleanupStagingAsync(source.Id, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "post-delete cleanup for source {SourceId} failed — source is deleted, leftovers may remain", id);
        }

        // RF-008: a deleted source must disappear from cached search results —
        // bump the index-version token the same way a sync does.
        if (cache is not null)
        {
            await Caching.SafeCache.SetStringAsync(cache, Caching.CacheKeys.IndexVersion,
                Guid.NewGuid().ToString("N"), null,
                log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeSourceService>.Instance, ct);
            if (invalidationBus is not null)
                await invalidationBus.PublishAsync("index-version", ct);
        }
        await NotifyCatalogChanged(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<KnowledgeSourceDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var source = await db.Sources.FindAsync([id], ct);
        if (source is null)
            return ServiceResult<KnowledgeSourceDto>.Fail(404, "Source not found");
        source.IsActive = active;
        await db.SaveChangesAsync(ct);
        await NotifyCatalogChanged(ct);
        return ServiceResult<KnowledgeSourceDto>.Ok(ToDto(source));
    }

    /// <summary>Notion secret check (SPEC-20260919-notion-connector RF-001) and
    /// cloud credential check (SPEC-20260924-cloud-storage-connectors RF-006):
    /// a config carrying no usable secret field is only valid when an encrypted
    /// secret already exists for this source — <c>hasKey:true</c> without a
    /// stored secret is rejected, otherwise the source could never sync.</summary>
    private async Task<string?> ValidateConnectorSecretAsync(
        KnowledgeSource source, JsonObject? configuration, CancellationToken ct)
    {
        if (configuration is null)
            return null;

        if (source.SourceType == SourceType.Notion)
        {
            var usable = configuration["token"] is JsonValue tv
                && tv.TryGetValue<string>(out var token)
                && token.Length > 0 && token != "***";
            if (usable)
                return null;
            return await secrets.GetAsync(Ingestion.Connectors.NotionConnector.SecretKey(source.Id), ct) is null
                ? "Configuration key 'token' is required for Notion — no stored token for this source"
                : null;
        }

        if (source.SourceType == SourceType.SqlDatabase)
        {
            if (UsableSecret(configuration, "connectionString"))
                return null;
            return await secrets.GetAsync(Ingestion.Connectors.SqlDatabaseConnector.SecretKey(source.Id), ct) is null
                ? "Configuration key 'connectionString' is required for SqlDatabase — no stored secret for this source"
                : null;
        }

        // SPEC-20260927-restapi-sqldatabase-connectors RF-003: headers are
        // optional, but hasKey:true without a stored secret is a stale update
        // that could never sync.
        if (source.SourceType == SourceType.RestApi)
        {
            var hasHeaders = configuration["hasKey"] is JsonValue hv
                && hv.TryGetValue<bool>(out var flagged) && flagged;
            if (!hasHeaders)
                return null;
            return await secrets.GetAsync(Ingestion.Connectors.RestApiConnector.SecretKey(source.Id), ct) is null
                ? "Configuration key 'headers' marked as stored (hasKey) but no stored headers for this source"
                : null;
        }

        if (source.SourceType is SourceType.AwsS3 or SourceType.OciStorage)
        {
            if (UsableSecret(configuration, "secretAccessKey"))
                return null;
            return await secrets.GetAsync(CloudSecretKey(source.SourceType, source.Id), ct) is null
                ? $"Configuration key 'secretAccessKey' is required for {source.SourceType} — no stored secret for this source"
                : null;
        }

        if (source.SourceType == SourceType.AzureFiles)
        {
            if (UsableSecret(configuration, "connectionString") || UsableSecret(configuration, "accountKey"))
                return null;
            return await secrets.GetAsync(CloudSecretKey(source.SourceType, source.Id), ct) is null
                ? "A 'connectionString' or 'accountKey' is required for AzureFiles — no stored secret for this source"
                : null;
        }

        return null;
    }

    private static bool UsableSecret(JsonObject configuration, string key) =>
        configuration[key] is JsonValue v
        && v.TryGetValue<string>(out var s)
        && s.Length > 0 && s != "***";

    /// <summary>Secret-store key for a cloud source — one slot per source.</summary>
    private static string CloudSecretKey(SourceType type, Guid sourceId) => type switch
    {
        SourceType.AwsS3 => $"s3:{sourceId}",
        SourceType.AzureFiles => $"azure:{sourceId}",
        SourceType.OciStorage => $"oci:{sourceId}",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>All secret-store keys a source type may hold (delete cleanup).</summary>
    private static IEnumerable<string> CloudSecretKeys(SourceType type, Guid sourceId)
    {
        if (type is SourceType.AwsS3 or SourceType.AzureFiles or SourceType.OciStorage)
            yield return CloudSecretKey(type, sourceId);
    }

    /// <summary>Moves the connector's secret field to the encrypted store —
    /// <c>configuration.apiKey</c> for McpProxy (SPEC-20260917 RF-003) and
    /// <c>configuration.token</c> for Notion (SPEC-20260919 RF-005): the
    /// persisted configuration keeps only a non-sensitive <c>hasKey</c> flag.
    /// An absent key keeps the stored secret; <c>"***"</c> (the redaction
    /// marker echoed by edited forms) also keeps it; empty removes it.</summary>
    private async Task PersistProxySecretAsync(KnowledgeSource source, JsonObject? configuration, CancellationToken ct)
    {
        var (configKey, secretKey) = source.SourceType switch
        {
            SourceType.McpProxy => ("apiKey", McpProxySession.SecretKey(source.Id)),
            SourceType.Notion => ("token", Ingestion.Connectors.NotionConnector.SecretKey(source.Id)),
            SourceType.GoogleDrive => ("apiKey", Ingestion.Connectors.GoogleDriveSharedConnector.SecretKey(source.Id)),
            // SPEC-20260927-restapi-sqldatabase-connectors RF-003/RF-006: the
            // RestApi headers JSON and the SqlDatabase connection string move
            // to the encrypted store — config persists only hasKey.
            SourceType.RestApi => ("headers", Ingestion.Connectors.RestApiConnector.SecretKey(source.Id)),
            SourceType.SqlDatabase => ("connectionString", Ingestion.Connectors.SqlDatabaseConnector.SecretKey(source.Id)),
            _ => (null, null)
        };
        if (configKey is not null && secretKey is not null && configuration is not null)
        {
            var singleConfig = JsonNode.Parse(source.ConfigurationJson ?? "{}") as JsonObject ?? new JsonObject();
            singleConfig.Remove(configKey);

            if (configuration.TryGetPropertyValue(configKey, out var keyNode)
                && keyNode?.GetValue<string>() is { } key
                && key != "***")
            {
                if (key.Length == 0)
                {
                    await secrets.RemoveAsync(secretKey, ct);
                    singleConfig["hasKey"] = false;
                }
                else
                {
                    await secrets.SetAsync(secretKey, key, ct);
                    singleConfig["hasKey"] = true;
                }
            }
            else
            {
                singleConfig["hasKey"] = await secrets.GetAsync(secretKey, ct) is not null;
            }

            source.ConfigurationJson = singleConfig.ToJsonString();
            return;
        }

        // SPEC-20260924-cloud-storage-connectors RF-006: cloud credential fields
        // move to the encrypted store — AzureFiles packs both optional fields
        // into a JSON payload under one slot.
        if (source.SourceType is SourceType.AwsS3 or SourceType.AzureFiles or SourceType.OciStorage
            && configuration is not null)
        {
            var fields = source.SourceType == SourceType.AzureFiles
                ? new[] { "connectionString", "accountKey" }
                : new[] { "secretAccessKey" };
            var cloudKey = CloudSecretKey(source.SourceType, source.Id);
            var config = JsonNode.Parse(source.ConfigurationJson ?? "{}") as JsonObject ?? new JsonObject();
            foreach (var f in fields)
                config.Remove(f);

            var changed = fields.Any(f =>
                configuration.TryGetPropertyValue(f, out var n)
                && n?.GetValue<string>() is { } v && v != "***");
            if (changed)
            {
                var payload = new JsonObject();
                var anyValue = false;
                foreach (var f in fields)
                {
                    if (configuration.TryGetPropertyValue(f, out var n)
                        && n?.GetValue<string>() is { } fv
                        && fv != "***" && fv.Length > 0)
                    {
                        payload[f] = fv;
                        anyValue = true;
                    }
                }
                if (anyValue)
                {
                    await secrets.SetAsync(cloudKey,
                        source.SourceType == SourceType.AzureFiles ? payload.ToJsonString() : payload[fields[0]]!.GetValue<string>(), ct);
                    config["hasKey"] = true;
                }
                else
                {
                    await secrets.RemoveAsync(cloudKey, ct);
                    config["hasKey"] = false;
                }
            }
            else
            {
                config["hasKey"] = await secrets.GetAsync(cloudKey, ct) is not null;
            }

            source.ConfigurationJson = config.ToJsonString();
        }
    }

    /// <summary>Catalog mutations must never fail the REST call — notification is best-effort.</summary>
    private async Task NotifyCatalogChanged(CancellationToken ct)
    {
        try { await catalogNotifier.NotifyToolsChangedAsync(ct); }
        catch { /* connected-client notification is advisory */ }
    }

    public async Task<IReadOnlyList<KnowledgeDocumentDto>?> ListDocumentsAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Sources.AnyAsync(s => s.Id == id, ct))
            return null;
        return await db.Documents.AsNoTracking()
            .Where(d => d.KnowledgeSourceId == id)
            .OrderBy(d => d.Title)
            .Select(d => new KnowledgeDocumentDto
            {
                Id = d.Id,
                SourceId = d.KnowledgeSourceId,
                Title = d.Title,
                UriReference = d.UriReference,
                ChunkCount = d.Chunks.Count,
                IndexedAt = d.IndexedAt
            })
            .ToListAsync(ct);
    }

    private static string? Validate(string? name, SourceType type, JsonObject? configuration)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is required";
        if (!Enum.IsDefined(type))
            return $"Invalid source type '{type}'";

        if (configuration is null)
            return $"Configuration is required for {type} (expects {string.Join(", ", RequiredKeys[type])})";

        foreach (var key in RequiredKeys[type])
        {
            if (configuration[key] is null || string.IsNullOrWhiteSpace(configuration[key]?.GetValue<string>()))
                return $"Configuration key '{key}' is required for {type}";
        }

        if (type == SourceType.GoogleDrive
            && !Ingestion.Connectors.GoogleDriveApiClient.TryParseSharedUrl(
                configuration["sharedUrl"]?.GetValue<string>(), out _, out _))
            return "Configuration key 'sharedUrl' must be a Google Drive /folders/ or /file/d/ share link";

        if (type == SourceType.McpProxy)
        {
            var endpoint = configuration["endpoint"]?.GetValue<string>();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
                return "Configuration key 'endpoint' must be an absolute http(s) URI for McpProxy";
            if (configuration["transport"]?.GetValue<string>()?.ToLowerInvariant()
                    is not (null or "auto" or "http" or "sse"))
                return "Configuration key 'transport' must be auto|http|sse for McpProxy";
        }

        if (type == SourceType.RssFeed)
        {
            var feedUrl = configuration["feedUrl"]?.GetValue<string>();
            if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var feedUri)
                || feedUri.Scheme is not ("http" or "https"))
                return "Configuration key 'feedUrl' must be an absolute http(s) URI for RssFeed";
        }

        if (type == SourceType.YouTube)
        {
            var language = configuration["language"]?.GetValue<string>();
            if (language is { Length: > 0 } lang)
            {
                var valid = System.Text.RegularExpressions.Regex.IsMatch(lang, @"^[a-zA-Z-]{2,8}$");
                if (!valid)
                    return "Configuration key 'language' must be 2-8 chars of letters and hyphens (e.g. 'pt', 'pt-BR', 'en')";
            }
        }

        if (type == SourceType.RestApi)
        {
            var restEndpoint = configuration["endpoint"]?.GetValue<string>();
            if (!Uri.TryCreate(restEndpoint, UriKind.Absolute, out var restUri)
                || restUri.Scheme is not ("http" or "https"))
                return "Configuration key 'endpoint' must be an absolute http(s) URI for RestApi";

            if (configuration["headers"] is JsonValue headersValue
                && headersValue.TryGetValue<string>(out var headers)
                && !string.IsNullOrWhiteSpace(headers) && headers != "***")
            {
                try
                {
                    if (JsonNode.Parse(headers) is not JsonObject)
                        return "Configuration key 'headers' must be a JSON object of header names to values";
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return "Configuration key 'headers' must be valid JSON like {\"Authorization\":\"Bearer …\"}";
                }
            }
        }

        if (type == SourceType.SqlDatabase)
        {
            var provider = configuration["provider"]?.GetValue<string>();
            if (provider?.ToLowerInvariant() is not ("sqlite" or "postgres"))
                return "Configuration key 'provider' must be 'sqlite' or 'postgres' for SqlDatabase";

            if (configuration["query"] is JsonValue queryValue
                && queryValue.TryGetValue<string>(out var sqlQuery)
                && !string.IsNullOrWhiteSpace(sqlQuery))
            {
                var (ok, reason) = Ingestion.Connectors.SqlQueryGuard.Validate(sqlQuery);
                if (!ok)
                    return $"Configuration key 'query' rejected (read-only queries only): {reason}";
            }
        }

        if (type == SourceType.Notion)
        {
            var tokenPresent = configuration["token"] is JsonValue tv
                && tv.TryGetValue<string>(out var _);
            var hasKey = configuration["hasKey"] is JsonValue hk
                && hk.TryGetValue<bool>(out var b) && b;
            if (!tokenPresent && !hasKey)
                return "Configuration key 'token' is required for Notion (integration token)";

            var baseUrlNode = configuration["apiBaseUrl"];
            if (baseUrlNode is not null
                && (baseUrlNode is not JsonValue bv
                    || !bv.TryGetValue<string>(out var baseUrl)
                    || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var bu)
                    || (bu.Scheme != "http" && bu.Scheme != "https")))
                return "Configuration key 'apiBaseUrl' must be an absolute http(s) URI for Notion";

            var maxPagesNode = configuration["maxPages"];
            if (maxPagesNode is not null
                && (maxPagesNode is not JsonValue jv
                    || !jv.TryGetValue<int>(out var mp) || mp is < 1 or > 1000))
                return "Configuration key 'maxPages' must be an integer between 1 and 1000 for Notion";
        }
        return null;
    }

    private static KnowledgeSourceDto ToDto(KnowledgeSource s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Description = s.Description,
        Type = s.SourceType,
        Configuration = Redact(s.ConfigurationJson),
        IsActive = s.IsActive,
        AutoSyncEnabled = s.AutoSyncEnabled,
        SyncIntervalMinutes = s.SyncIntervalMinutes,
        CreatedAt = s.CreatedAt,
        LastSyncAt = s.LastSyncAt,
        LastSyncStatus = s.LastSyncStatus,
        LastError = s.LastError
    };

    /// <summary>Strip sensitive keys so API responses never echo secrets (SPEC-02 §Guardrails).</summary>
    public static JsonObject? Redact(string? configurationJson)
    {
        if (string.IsNullOrEmpty(configurationJson))
            return null;
        try
        {
            var node = JsonNode.Parse(configurationJson) as JsonObject;
            if (node is null)
                return null;
            var clone = (JsonObject)node.DeepClone();
            foreach (var key in SensitiveKeys)
            {
                if (clone.ContainsKey(key))
                    clone[key] = "***";
            }
            return clone;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
