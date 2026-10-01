namespace KnowledgeHub.Shared.Contracts;

/// <summary>Retrieval mode (SPEC-20260914-hybrid-retrieval RF-002).</summary>
public enum SearchMode
{
    /// <summary>Vector similarity fused with FTS5 lexical rank via RRF (k=60).</summary>
    Hybrid,
    /// <summary>Vector similarity only — the pre-hybrid behavior.</summary>
    Semantic,
    /// <summary>SQLite FTS5 lexical matching only.</summary>
    Lexical
}

/// <summary>Optional rank provenance for a fused result (debug/audit).</summary>
public sealed record SearchScoreBreakdown
{
    public int? VectorRank { get; init; }
    public int? LexicalRank { get; init; }
    public double Fused { get; init; }
    /// <summary>Fused normalized to 0–1 where 1.0 = ranked #1 on every fused
    /// arm (denominator = numArms/(K+1)). Scale used by `MinScores.Final`.
    /// 0 when no fused ranking produced the hit.</summary>
    public double Normalized { get; init; }
    /// <summary>Reranker score (0–10) when Search:Rerank:Enabled
    /// (SPEC-20260923-retrieval-quality RF-002).</summary>
    public double? Rerank { get; init; }
    /// <summary>SPEC-20260924-graph-expanded-retrieval RF-002: rank on the
    /// knowledge-graph arm (entity evidence chunks) when present.</summary>
    public int? GraphRank { get; init; }
    /// <summary>SPEC-20260924-query-expansion-hyde RF-004: the expansion variant
    /// that surfaced this hit ("hyde" for the hypothetical-document arm);
    /// null when expansion is off or the hit came from the original query.</summary>
    public string? ExpandedFrom { get; init; }
}

/// <summary>Optional metadata filters for search/ask
/// (SPEC-20260923-retrieval-quality RF-003). All fields optional; an empty
/// object behaves like no filters.</summary>
public sealed record SearchFilter
{
    /// <summary>Connector type name, e.g. "ObsidianVault", "WebPage".</summary>
    public string? SourceType { get; init; }
    /// <summary>Prefix match on the document URI/path.</summary>
    public string? PathPrefix { get; init; }
    /// <summary>ISO-8601 date — only documents indexed at/after it.</summary>
    public string? IndexedAfter { get; init; }
    /// <summary>BCP-47 tag matched against document language metadata when present.</summary>
    public string? Language { get; init; }
    /// <summary>SPEC-20260924-query-expansion-hyde RF-003: per-call expansion
    /// override — off|multi|hyde|both. Null = use configured default.</summary>
    public string? Expand { get; init; }
    /// <summary>SPEC-20260924-hierarchical-retrieval RF-001: per-hit context
    /// expansion — none|window|section. Null/none = unchanged behavior.</summary>
    public string? ContextExpand { get; init; }
    /// <summary>SPEC-20260924-graph-expanded-retrieval RF-004: per-call toggle
    /// for the graph arm — null = configured default.</summary>
    public bool? UseGraph { get; init; }
    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut RF-004: per-call
    /// window breadth (0–3). A value &gt;0 implies <c>contextExpand=window</c>;
    /// 0 disables neighbour expansion. Null = configured default.</summary>
    public int? WindowSize { get; init; }
    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut RF-004: result
    /// limit mode — fixed|autocut. Null = configured default.</summary>
    public string? LimitMode { get; init; }
    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut RF-004: autocut
    /// elbow sensitivity (1–3; cut at the N-th abrupt score drop).</summary>
    public int? AutocutSensitivity { get; init; }
    /// <summary>SPEC-20260927-hierarchical-filter-relaxation-and-multiquery RF-001:
    /// caller-supplied sub-queries — each runs as an extra retrieval arm fused via
    /// RRF (max 4; blank entries are dropped).</summary>
    public IReadOnlyList<string>? SubQueries { get; init; }
    /// <summary>SPEC-20260927-hierarchical-filter-relaxation-and-multiquery RF-002:
    /// per-call opt-out of the hierarchical scope fallback (default: server config).</summary>
    public bool? AllowRelaxation { get; init; }
    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-001: search depth —
    /// low|mid|high. Low = narrow candidate pool, no expansion, no grading
    /// retries; mid = default pool without expansion; high (default) = full
    /// pipeline. Null = high.</summary>
    public string? Budget { get; init; }
    /// <summary>RF-003: per-stage score floors — abstain instead of returning
    /// weak evidence when the final floor empties the result set.</summary>
    public SearchMinScores? MinScores { get; init; }
    /// <summary>RF-004: ISO-8601 — boost hits whose document was indexed inside
    /// the window (boost, not filter). Paired with <see cref="TemporalEnd"/>.</summary>
    public string? TemporalStart { get; init; }
    /// <summary>RF-004: ISO-8601 window end (inclusive).</summary>
    public string? TemporalEnd { get; init; }
}

/// <summary>RF-003: per-stage score floors (SPEC-20261001-mcp-recall-ergonomics).
/// `semantic` prunes the vector arm (cosine 0–1) pre-fusion; `lexical` prunes
/// the FTS arm as a fraction of that arm's best BM25 (0–1); `final` filters
/// post-fusion against the normalized fused score (0–1, see
/// <see cref="SearchScoreBreakdown.Normalized"/>) — an empty result makes
/// ask_knowledge abstain.</summary>
public sealed record SearchMinScores
{
    public double? Semantic { get; init; }
    public double? Lexical { get; init; }
    public double? Final { get; init; }
}

/// <summary>One ranked chunk hit from semantic search (SPEC-02 RF-004).</summary>
public sealed record SearchResultItem
{
    public required string ChunkText { get; init; }
    public required string DocumentTitle { get; init; }
    public required string SourceName { get; init; }
    public required Guid SourceId { get; init; }
    public required double Score { get; init; }
    public required string UriReference { get; init; }
    /// <summary>Connector type of the owning source. Non-required so older cached
    /// payloads still deserialize (SPEC-20260922-tool-descriptions-en-us RF-003).</summary>
    public SourceType SourceType { get; init; }
    /// <summary>Rank provenance when retrieved in hybrid/lexical mode; null for plain semantic.</summary>
    public SearchScoreBreakdown? ScoreBreakdown { get; init; }
    /// <summary>Suspicion flags carried into the prompt when exclusion is
    /// disabled (SPEC-20260923-prompt-injection-guard RF-004); null = clean.</summary>
    public string? SuspicionFlags { get; init; }
    /// <summary>True when the chunk was flagged by the security scan and kept
    /// (only possible with ExcludeFlagged=false) — SPEC-20260923-flagged-chunk-badge
    /// RF-001. Computed from <see cref="SuspicionFlags"/> so the two never drift.</summary>
    public bool SecurityFlagged => SuspicionFlags is not null;
    /// <summary>Stable chunk id (SPEC-20260923-retrieval-quality RF-004).</summary>
    public Guid? ChunkId { get; init; }
    /// <summary>Owning document id.</summary>
    public Guid? DocumentId { get; init; }
    /// <summary>Ordinal position inside the owning document
    /// (SPEC-20260924-hierarchical-retrieval — needed for window expansion).</summary>
    public int? ChunkIndex { get; init; }
    /// <summary>Derived provenance metadata (sourceType, path, chunkKind, symbolPath).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    /// <summary>When the owning document was last indexed.</summary>
    public DateTimeOffset? IndexedAt { get; init; }
    /// <summary>SPEC-20260924-contextual-chunk-enrichment RF-003: heading chain
    /// this chunk belongs to — lets callers/agent see where in the document the
    /// passage lives. Null when outside any section.</summary>
    public string? SectionPath { get; init; }
    /// <summary>SPEC-20260924-hierarchical-retrieval RF-002: surrounding context
    /// (neighbouring chunks or the parent section) — never part of ranking or
    /// citation identity; the hit itself stays in <see cref="ChunkText"/>.</summary>
    public string? Context { get; init; }
    /// <summary>Knowledge-graph entity names evidenced by this chunk — feed these
    /// names to the find_* graph tools (SPEC-20260924-graph-tool-discovery RF-001).
    /// Null when GraphRAG is disabled or the chunk has no graph evidence.</summary>
    public IReadOnlyList<string>? Components { get; init; }
    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut RF-002: indices of
    /// the same-document chunks stitched into <see cref="Context"/> (excludes the
    /// hit itself). Null when no expansion ran.</summary>
    public IReadOnlyList<int>? ExpandedChunkIndices { get; init; }
    /// <summary>True when surrounding context was stitched in (window retrieval).</summary>
    public bool WindowExpanded => Context is not null;
    /// <summary>SPEC-20260927-hierarchical-filter-relaxation-and-multiquery RF-002:
    /// the hit came from a relaxed scope, not the strict filter requested.</summary>
    public bool IsRelaxed { get; init; }
    /// <summary>Effective scope that surfaced a relaxed hit — e.g.
    /// <c>sourceType=DocumentFile</c> or <c>global</c>. Null for strict hits.</summary>
    public string? RelaxedScope { get; init; }
}

public sealed record SearchResponse
{
    public required IReadOnlyList<SearchResultItem> Results { get; init; }
    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut: number of hits
    /// actually returned after the limit mode ran.</summary>
    public int? TotalMatches { get; init; }
    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut: the limit mode
    /// applied to this result set (fixed | autocut).</summary>
    public string? LimitModeApplied { get; init; }
    /// <summary>SPEC-20260927-hierarchical-filter-relaxation-and-multiquery RF-003:
    /// true when any hit came from a relaxed scope.</summary>
    public bool? FilterRelaxed { get; init; }
    /// <summary>The scope the caller originally requested (e.g. <c>sourceId=…</c>).</summary>
    public string? OriginalFilter { get; init; }
    /// <summary>The effective scope that produced relaxed hits
    /// (e.g. <c>sourceType=…</c> or <c>global</c>).</summary>
    public string? AppliedFilter { get; init; }
}

/// <summary>POST /api/search body (SPEC-20260923-retrieval-quality §5).</summary>
public sealed record SearchRequest
{
    public required string Query { get; init; }
    public int? TopK { get; init; }
    public Guid? SourceId { get; init; }
    public string? Mode { get; init; }
    public SearchFilter? Filters { get; init; }
}
