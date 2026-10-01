using KnowledgeHub.Server.Embeddings;
using Microsoft.Extensions.Caching.Distributed;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>Pipeline dependencies of <see cref="IngestionService"/> — grouped to
/// keep the service constructor under the 7-parameter guideline (S107).</summary>
public sealed record IngestionServiceDeps(
    IServiceScopeFactory ScopeFactory,
    IEmbeddingProvider Embeddings,
    IEnumerable<Connectors.ISourceConnector> Connectors,
    IDistributedCache Cache,
    Security.IContentSanitizer Sanitizer,
    Settings.IGraphSettingsService GraphSettings,
    Settings.IEmbeddingSettingsService EmbeddingSettings);
