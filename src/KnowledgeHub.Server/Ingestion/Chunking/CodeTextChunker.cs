using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260923-code-aware-chunking RF-002: brace/indent-heuristic chunker for
/// C#-like languages. Splits at member boundaries inside the outermost type —
/// a member stays whole when it fits; oversized members split on blank lines
/// with a <c>// Ns.Type.Member (part k/n)</c> preamble. No Roslyn.
/// </summary>
public sealed partial class CodeTextChunker : ITextChunker
{
    public static readonly CodeTextChunker Instance = new();

    private const int CharsPerToken = 4;

    public ChunkKind Kind => ChunkKind.Code;

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"(?:class|struct|interface|record|enum)\s+(\w+)")]
    private static partial Regex TypeRegex();

    // Member-ish line: attribute, access/modifier keyword, or a signature
    // ending in '(' — only at the type-body depth.
    [GeneratedRegex(@"^\s*(\[|(public|private|protected|internal|static|sealed|abstract|virtual|override|async|readonly|extern|new|partial|unsafe|volatile)\b)")]
    private static partial Regex MemberStartRegex();

    [GeneratedRegex(@"(\w+)\s*\(")]
    private static partial Regex MethodNameRegex();

    [GeneratedRegex(@"(\w+)\s*[{=;]")]
    private static partial Regex MemberNameRegex();

    private sealed record Block(string Text, string? SymbolPath);

    public IReadOnlyList<ChunkPiece> Chunk(string text, int maxTokens, int overlapTokens)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var maxChars = maxTokens * CharsPerToken;
        var blocks = SplitIntoBlocks(text);
        var pieces = Pack(blocks, maxChars);

        var result = new List<ChunkPiece>();
        foreach (var piece in pieces)
        {
            if (piece.Text.Length <= maxChars)
            {
                result.Add(new ChunkPiece(piece.Text, piece.SymbolPath));
                continue;
            }
            // Oversized member → blank-line split with symbol preamble.
            var parts = SplitOnBlankLines(piece.Text, maxChars);
            for (var i = 0; i < parts.Count; i++)
            {
                var path = piece.SymbolPath ?? "code";
                var preamble = $"// {path} (part {i + 1}/{parts.Count})\n";
                var body = parts[i];
                while (body.Length + preamble.Length > maxChars && body.Length > 0)
                {
                    var take = maxChars - preamble.Length;
                    result.Add(new ChunkPiece(preamble + body[..take], piece.SymbolPath));
                    body = body[take..];
                }
                if (body.Trim().Length > 0)
                    result.Add(new ChunkPiece(preamble + body, piece.SymbolPath));
            }
        }
        return result;
    }

    /// <summary>Splits source into member-aligned blocks with symbol paths.</summary>
    private static List<Block> SplitIntoBlocks(string text)
    {
        var blocks = new List<Block>();
        var pending = new StringBuilder();
        var ns = "";
        var type = "";
        var typeBodyDepth = -1;
        var depth = 0;

        void Flush()
        {
            var t = pending.ToString().Trim();
            if (t.Length > 0)
                blocks.Add(new Block(t, SymbolOf(t, ns, type)));
            pending.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            // Only the outermost type drives member splitting — nested types
            // stay inside the enclosing member block.
            var typeMatch = TypeRegex().Match(line);
            var isTypeDecl = typeMatch.Success && depth <= 2 && typeBodyDepth < 0;
            if (NamespaceRegex().Match(line) is { Success: true } nm)
                ns = nm.Groups[1].Value;
            if (isTypeDecl)
            {
                type = typeMatch.Groups[1].Value;
                // Members live one depth level inside the type's braces.
                typeBodyDepth = depth + 1;
            }

            if (typeBodyDepth >= 0
                && depth == typeBodyDepth
                && pending.Length > 0
                && MemberStartRegex().IsMatch(trimmed)
                && trimmed.Length > 0)
            {
                Flush();
            }

            pending.AppendLine(line);
            depth += Count(line, '{') - Count(line, '}');
        }
        Flush();
        return blocks;
    }

    private static string? SymbolOf(string block, string ns, string type)
    {
        // Only the first meaningful line names the member — scanning the whole
        // block would leak identifiers from nested bodies.
        var firstLine = block.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        string? member = null;
        if (!firstLine.StartsWith("using ", StringComparison.Ordinal)
            && !firstLine.StartsWith("namespace ", StringComparison.Ordinal))
        {
            var match = MethodNameRegex().Match(firstLine);
            if (!match.Success)
                match = MemberNameRegex().Match(firstLine);
            member = match.Success ? match.Groups[1].Value : null;
        }
        var path = string.Join('.', new[] { ns, type, member }.Where(s => !string.IsNullOrEmpty(s)));
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// Greedily merges adjacent blocks up to the char budget — but never merges
    /// blocks with different member paths (each chunk must name one symbol).
    /// </summary>
    private static List<Block> Pack(List<Block> blocks, int maxChars)
    {
        var packed = new List<Block>();
        var current = new StringBuilder();
        string? currentPath = null;

        foreach (var block in blocks)
        {
            var samePath = currentPath is null || block.SymbolPath is null
                || block.SymbolPath == currentPath;
            if (current.Length > 0
                && (!samePath || current.Length + block.Text.Length + 2 > maxChars))
            {
                packed.Add(new Block(current.ToString().Trim(), currentPath));
                current.Clear();
                currentPath = null;
            }
            currentPath ??= block.SymbolPath;
            current.Append(block.Text).Append("\n\n");
        }
        if (current.ToString().Trim().Length > 0)
            packed.Add(new Block(current.ToString().Trim(), currentPath));
        return packed;
    }

    private static List<string> SplitOnBlankLines(string text, int maxChars)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var para in paragraphs)
        {
            if (current.Length > 0 && current.Length + para.Length + 2 > maxChars)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            current.Append(para).Append("\n\n");
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts.Count > 0 ? parts : [text];
    }

    private static int Count(string line, char ch) => line.Count(c => c == ch);
}
