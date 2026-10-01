using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.VectorStore;
using Microsoft.Extensions.Caching.Distributed;

namespace KnowledgeHub.Server.Services;

/// <summary>Retrieval-layer dependencies of <see cref="SearchService"/> — grouped
/// to keep the service constructor under the 7-parameter guideline (S107).</summary>
public sealed record SearchRetrievalDeps(
    KnowledgeHubDbContext Db,
    IEmbeddingProvider Embeddings,
    IEmbeddingProviderResolver EmbeddingsResolver,
    IVectorStore Vectors,
    ILexicalSearchService Lexical,
    IDistributedCache Cache);

/// <summary>Query-pipeline dependencies of <see cref="SearchService"/> — rewrite,
/// expansion, graph linking, rerank, caller scope and graph settings.</summary>
public sealed record SearchPipelineDeps(
    IQueryRewriter Rewriter,
    IQueryExpander Expander,
    Graph.GraphEntityLinker GraphLinker,
    IReranker Reranker,
    Auth.ICallerScopeProvider CallerScope,
    Settings.IGraphSettingsService GraphSettings);
