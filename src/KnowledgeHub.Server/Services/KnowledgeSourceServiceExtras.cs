namespace KnowledgeHub.Server.Services;

/// <summary>Optional dependencies of <see cref="KnowledgeSourceService"/> —
/// grouped so the service constructor stays under the 7-parameter guideline
/// (S107). Every member is nullable; DI fills what is registered.</summary>
public sealed record KnowledgeSourceServiceExtras(
    Ingestion.Staging.IStagingStorageService? Staging = null,
    VectorStore.IVectorStore? Vectors = null,
    ILogger<KnowledgeSourceService>? Log = null,
    Microsoft.Extensions.Caching.Distributed.IDistributedCache? Cache = null,
    Caching.ICacheInvalidationBus? InvalidationBus = null);
