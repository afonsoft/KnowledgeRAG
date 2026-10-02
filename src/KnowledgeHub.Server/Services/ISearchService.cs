using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Services;

/// <summary>Unified search over active sources (SPEC-02 RF-004, SPEC-20260914-hybrid-retrieval).</summary>
public interface ISearchService
{
    /// <summary>Runs the hybrid retrieval pipeline (rewrite → FTS/vector → RRF → MMR).</summary>
    /// <param name="sourceId">Restricts the search to one source; null searches all active sources.</param>
    /// <param name="mode">Hybrid (default), Semantic (pre-hybrid behavior) or Lexical.</param>
    /// <param name="filter">Validated metadata filters (SPEC-20260923-retrieval-quality RF-003).</param>
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query, int topK, Guid? sourceId = null,
        SearchMode mode = SearchMode.Hybrid, Search.ResolvedSearchFilter? filter = null,
        string? conversationContext = null, CancellationToken ct = default);
}
