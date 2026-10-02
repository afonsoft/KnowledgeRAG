using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260923-code-aware-chunking: adapter exposing <see cref="MarkdownChunker"/>
/// through <see cref="ITextChunker"/>. Output text is byte-identical to the
/// legacy chunker (regression guarantee) — SymbolPath is always null.
/// Also serves as the prose fallback.
/// </summary>
public sealed class MarkdownTextChunker : ITextChunker
{
    private MarkdownTextChunker(ChunkKind kind) => Kind = kind;

    public static readonly MarkdownTextChunker Markdown = new(ChunkKind.Markdown);
    public static readonly MarkdownTextChunker Prose = new(ChunkKind.Prose);

    public static MarkdownTextChunker For(ChunkKind kind) =>
        kind == ChunkKind.Prose ? Prose : Markdown;

    public ChunkKind Kind { get; }

    public IReadOnlyList<ChunkPiece> Chunk(string text, int maxTokens, int overlapTokens)
    {
        var pieces = MarkdownChunker.Chunk(text, maxTokens, overlapTokens);
        // SPEC-20260924-contextual-chunk-enrichment RF-001: track the heading
        // stack across consecutive pieces — a piece whose own text starts a
        // section updates the path; pieces without headers inherit the path of
        // the section they continue.
        var stack = new List<(int Level, string Title)>();
        return pieces.Select(p => new ChunkPiece(p, SectionPath: SectionPathFor(p, stack))).ToList();
    }

    /// <summary>First header inside <paramref name="piece"/> (post stack-update)
    /// defines its section; no header → current stack. Handles mid-piece
    /// headers too — they update the stack for subsequent pieces.</summary>
    private static string? SectionPathFor(string piece, List<(int Level, string Title)> stack)
    {
        string? firstPath = null;
        foreach (var groups in piece.Split('\n')
            .Select(raw => HeaderLine().Match(raw.TrimStart()))
            .Where(m => m.Success)
            .Select(m => m.Groups))
        {
            var level = groups[1].Value.Length;
            var title = groups[2].Value.Trim();
            while (stack.Count > 0 && stack[^1].Level >= level)
                stack.RemoveAt(stack.Count - 1);
            stack.Add((level, title));
            firstPath ??= string.Join(" > ", stack.Select(s => s.Title));
        }
        return firstPath ?? (stack.Count > 0 ? string.Join(" > ", stack.Select(s => s.Title)) : null);
    }

    private static Regex HeaderLine() => _headerLine;
    private static readonly Regex _headerLine =
        new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
