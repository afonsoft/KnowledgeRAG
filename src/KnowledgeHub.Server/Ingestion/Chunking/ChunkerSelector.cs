using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260923-code-aware-chunking RF-001: deterministic mapping
/// document extension → <see cref="ChunkKind"/> → chunker instance.
/// Unknown/extensionless kinds fall back to prose (markdown behavior).
/// </summary>
public static class ChunkerSelector
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".js", ".ts", ".jsx", ".tsx", ".py", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".csx", ".fs", ".rb", ".php", ".swift",
        ".kt", ".kts", ".scala", ".sh", ".ps1", ".sql"
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".config",
        ".csproj", ".props", ".targets", ".slnx"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown", ".mdx" };

    /// <summary>Classifies a document by its URI/path extension.</summary>
    public static ChunkKind KindFor(string? uriOrPath)
    {
        var ext = Path.GetExtension(uriOrPath ?? "");
        if (MarkdownExtensions.Contains(ext)) return ChunkKind.Markdown;
        if (CodeExtensions.Contains(ext)) return ChunkKind.Code;
        if (ConfigExtensions.Contains(ext)) return ChunkKind.Config;
        return ChunkKind.Prose;
    }

    /// <summary>Resolves the chunker for a kind (prose falls back to markdown rules).</summary>
    public static ITextChunker For(ChunkKind kind) => kind switch
    {
        ChunkKind.Code => CodeTextChunker.Instance,
        ChunkKind.Config => ConfigTextChunker.Instance,
        _ => MarkdownTextChunker.For(kind)
    };

    /// <summary>Convenience: classify + chunk in one call. Documents whose text
    /// contains table blocks route to the layout-aware chunker automatically
    /// (SPEC-20260927-ragflow-vision-layout-chunking — auto selection).</summary>
    public static (ChunkKind Kind, IReadOnlyList<ChunkPiece> Pieces) Chunk(
        string? uriOrPath, string text, int maxTokens, int overlapTokens)
    {
        var kind = KindFor(uriOrPath);
        var chunker = kind is ChunkKind.Markdown or ChunkKind.Prose
            && TableChunkSplitter.ContainsTable(text)
                ? new VisionLayoutTextChunker(null, kind)
                : For(kind);
        return (kind, chunker.Chunk(text, maxTokens, overlapTokens));
    }

    /// <summary>
    /// SPEC-20260924-semantic-chunking RF-003: strategy-aware async entry point.
    /// <paramref name="strategy"/> = auto|semantic|visionlayout — <c>semantic</c>
    /// embeds sentences to find topic boundaries (prose/documents only; code/config
    /// always stay structural). <c>visionlayout</c> forces the layout-aware chunker
    /// (atomic tables, figure captions) and <c>auto</c> engages it whenever the
    /// text contains table blocks (SPEC-20260927-ragflow-vision-layout-chunking).
    /// Any failure falls back to the structural chunker for the classified kind —
    /// ingestion never aborts over chunking.
    /// </summary>
    public static async Task<(ChunkKind Kind, IReadOnlyList<ChunkPiece> Pieces)> ChunkAsync(
        string? uriOrPath, string text, int maxTokens, int overlapTokens,
        Embeddings.IEmbeddingProvider embeddings, IConfiguration configuration,
        string? strategy, ILogger logger, CancellationToken ct = default,
        IServiceProvider? services = null)
    {
        var kind = KindFor(uriOrPath);
        var effective = strategy
            ?? configuration.GetValue("Ingestion:Chunking:Strategy", "auto");

        if (kind is ChunkKind.Markdown or ChunkKind.Prose)
        {
            if (string.Equals(effective, "semantic", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var pieces = await new SemanticTextChunker(embeddings, configuration)
                        .ChunkAsync(text, maxTokens, overlapTokens, ct);
                    return (kind, pieces);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex,
                        "Semantic chunking failed for '{Uri}' — falling back to structural chunker", uriOrPath);
                }
            }

            // SPEC-20260927-ragflow-vision-layout-chunking: explicit strategy or
            // auto-detected table/layout-rich content → layout-aware chunker.
            if (string.Equals(effective, "visionlayout", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(effective, "auto", StringComparison.OrdinalIgnoreCase)
                    && TableChunkSplitter.ContainsTable(text)))
            {
                var chunker = new VisionLayoutTextChunker(
                    configuration, kind,
                    services?.GetService<Microsoft.Extensions.AI.IChatClient>(),
                    services?.GetService<IHttpClientFactory>(), logger);
                var pieces = await chunker.ChunkAsync(text, maxTokens, overlapTokens, ct);
                return (kind, pieces);
            }
        }

        return (kind, await For(kind).ChunkAsync(text, maxTokens, overlapTokens, ct));
    }

    /// <summary>
    /// SPEC-20260924-async-ingestion-queue RF-003: bump manually whenever any
    /// chunker's output shape changes (new structural metadata, different split
    /// rules). Compared against <c>KnowledgeDocument.ChunkerVersion</c> — a
    /// mismatch re-chunks even content-identical docs.
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>Hash of the chunking-relevant configuration — maxTokens, overlap,
    /// enrichment mode/min, per-source strategy.</summary>
    public static string ConfigHash(
        int maxTokens, int overlapTokens, string enrichmentMode, int enrichmentMinTokens, string? strategy)
    {
        var raw = $"v{CurrentVersion}|{maxTokens}|{overlapTokens}|{enrichmentMode}|{enrichmentMinTokens}|{strategy ?? "-"}";
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>Reads the per-source chunking override from
    /// <c>KnowledgeSource.ConfigurationJson</c> (<c>{"chunking":"semantic"}</c>);
    /// null/absent → global <c>Ingestion:Chunking:Strategy</c>.</summary>
    public static string? StrategyFor(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configurationJson);
            return doc.RootElement.TryGetProperty("chunking", out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
