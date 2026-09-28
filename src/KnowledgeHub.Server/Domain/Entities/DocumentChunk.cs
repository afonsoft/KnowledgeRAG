namespace KnowledgeHub.Server.Domain.Entities;

public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeDocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public required string TextContent { get; set; }
    /// <summary>Little-endian IEEE-754 float32 blob (BLOB storage decision — compact, fast).</summary>
    public byte[]? Embedding { get; set; }
    /// <summary>Identity of the model that produced <see cref="Embedding"/>, e.g. "deterministic:hash384".</summary>
    public string? EmbeddingModel { get; set; }
    /// <summary>SPEC-20260923-code-aware-chunking RF-004: chunker that produced
    /// this chunk — markdown|code|config|prose.</summary>
    public string ChunkKind { get; set; } = "markdown";
    /// <summary>Structural context — e.g. <c>Namespace.Type.Method</c> or
    /// <c>$.server.port</c>; null for prose/markdown chunks.</summary>
    public string? SymbolPath { get; set; }
    /// <summary>SPEC-20260924-contextual-chunk-enrichment RF-001: markdown
    /// heading chain this chunk belongs to ("Guide > Install"), null for
    /// chunks outside any section.</summary>
    public string? SectionPath { get; set; }
    /// <summary>SPEC-20260924-contextual-chunk-enrichment RF-002: text actually
    /// embedded and FTS-indexed — structural prefix + <see cref="TextContent"/>.
    /// Null when enrichment is off or skipped (tiny chunks); fall back to
    /// TextContent wherever this is read.</summary>
    public string? EnrichedText { get; set; }
    /// <summary>SPEC-20260923-prompt-injection-guard RF-002: comma-separated
    /// suspicion flags from the content sanitizer; null = clean.</summary>
    public string? SuspicionFlags { get; set; }
    /// <summary>SPEC-20260927-ragflow-vision-layout-chunking RF-003: structural
    /// metadata emitted by the chunker (is_table, table_headers,
    /// parent_section_title, is_figure); null for plain prose/code chunks.</summary>
    public string? MetadataJson { get; set; }

    public KnowledgeDocument Document { get; set; } = null!;
}
