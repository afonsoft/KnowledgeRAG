namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>Document kind driving chunker selection (SPEC-20260923-code-aware-chunking).</summary>
public enum ChunkKind
{
    Markdown,
    Code,
    Config,
    Prose
}

/// <summary>One chunk of text plus its structural context (symbol path / config path /
/// section path — SPEC-20260924-contextual-chunk-enrichment). <c>MetadataJson</c>
/// carries structural tags (SPEC-20260927-ragflow-vision-layout-chunking RF-003:
/// is_table, table_headers, table_rows_count, parent_section_title).</summary>
public sealed record ChunkPiece(
    string Text, string? SymbolPath = null, string? SectionPath = null, string? MetadataJson = null);

/// <summary>
/// Pluggable text chunker (SPEC-20260923-code-aware-chunking RF-001).
/// Implementations must be deterministic: same input ⇒ identical chunks.
/// </summary>
public interface ITextChunker
{
    ChunkKind Kind { get; }

    /// <summary>Splits <paramref name="text"/> into pieces ≤ roughly
    /// <paramref name="maxTokens"/> tokens (chars/4 approximation).</summary>
    IReadOnlyList<ChunkPiece> Chunk(string text, int maxTokens, int overlapTokens);

    /// <summary>Async variant — needed by embedding-driven chunkers
    /// (SPEC-20260924-semantic-chunking). Default delegates to <see cref="Chunk"/>.</summary>
    Task<IReadOnlyList<ChunkPiece>> ChunkAsync(
        string text, int maxTokens, int overlapTokens, CancellationToken ct = default) =>
        Task.FromResult(Chunk(text, maxTokens, overlapTokens));
}
