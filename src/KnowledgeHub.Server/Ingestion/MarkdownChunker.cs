using System.Text;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>
/// Header-aware Markdown chunker (SPEC-03 RF-002): splits on <c>#</c>/<c>##</c>/<c>###</c>
/// boundaries keeping the header with its section; oversized sections split on
/// paragraph boundaries; consecutive chunks share an overlap tail.
/// Tokens are approximated as chars/4. Never emits empty chunks.
/// </summary>
public static class MarkdownChunker
{
    private const int CharsPerToken = 4;

    public static IReadOnlyList<string> Chunk(string body, int maxTokens = 500, int overlapTokens = 50)
    {
        // RF-207: maxTokens=0 makes the hard-split increment 0 → infinite loop.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapTokens);
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var maxChars = maxTokens * CharsPerToken;
        var overlapChars = Math.Min(overlapTokens * CharsPerToken, maxChars / 2);

        var sections = SplitOnHeaders(body);
        var paragraphs = new List<string>();
        foreach (var section in sections)
        {
            if (section.Length <= maxChars)
            {
                paragraphs.Add(section);
            }
            else
            {
                foreach (var para in SplitOnParagraphs(section))
                    paragraphs.Add(para);
            }
        }

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var block in paragraphs)
        {
            var text = block.Trim();
            if (text.Length == 0)
                continue;

            // Block alone exceeds the budget → hard-split on character boundary.
            if (text.Length > maxChars)
            {
                FlushCurrent(current, chunks);
                for (var i = 0; i < text.Length; i += maxChars - overlapChars)
                {
                    var piece = text.Substring(i, Math.Min(maxChars, text.Length - i)).Trim();
                    if (piece.Length > 0)
                        chunks.Add(piece);
                    if (i + maxChars >= text.Length)
                        break;
                }
                continue;
            }

            if (current.Length + text.Length + 1 > maxChars && current.Length > 0)
            {
                FlushCurrent(current, chunks);
                // overlap: seed next chunk with the tail of the previous one
                var tail = TailOf(chunks[^1], overlapChars);
                if (tail.Length > 0)
                    current.Append(tail).Append("\n\n");
            }
            current.Append(text).Append("\n\n");
        }

        FlushCurrent(current, chunks);
        return chunks;
    }

    private static void FlushCurrent(StringBuilder current, List<string> chunks)
    {
        var text = current.ToString().Trim();
        if (text.Length > 0)
            chunks.Add(text);
        current.Clear();
    }

    private static string TailOf(string text, int chars)
    {
        if (chars <= 0 || text.Length <= chars)
            return chars <= 0 ? "" : text;
        // take a paragraph-aligned tail when possible
        var tail = text[^chars..];
        var nl = tail.IndexOf("\n\n", StringComparison.Ordinal);
        return (nl >= 0 ? tail[(nl + 2)..] : tail).Trim();
    }

    /// <summary>Split keeping each markdown header line attached to its section.</summary>
    private static IEnumerable<string> SplitOnHeaders(string body)
    {
        var sections = new List<StringBuilder> { new() };
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith('#') && line.TrimStart('#').StartsWith(' ') && sections[^1].Length > 0)
                sections.Add(new StringBuilder());
            sections[^1].AppendLine(rawLine);
        }
        return sections.Select(s => s.ToString()).Where(s => !string.IsNullOrWhiteSpace(s));
    }

    private static IEnumerable<string> SplitOnParagraphs(string text) =>
        text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
}
