using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.Output;

/// <summary>
/// Maps findings to GitHub review inline comments. GitHub accepts
/// <c>path + line + side=RIGHT</c> only when the line is part of the diff —
/// findings off-diff are dropped to the summary (spec §5 graceful degrade).
/// </summary>
public static class InlineCommentMapper
{
    public static IReadOnlyList<InlineComment> Map(IReadOnlyList<Finding> findings, PullRequestSignal signal)
    {
        var files = signal.Files.ToDictionary(f => f.Filename, StringComparer.Ordinal);
        var comments = new List<InlineComment>();

        foreach (var f in findings)
        {
            if (f.File is null || f.Line is null or <= 0) continue;
            if (!files.TryGetValue(f.File, out var file)) continue;
            if (file.Patch is null) continue;

            var position = PositionInPatch(file.Patch, f.Line.Value);
            if (position is null) continue;

            var cwe = f.Cwe is { } c ? $" · {c}" : "";
            var body = $"**{f.Kind}/{f.Severity}**{cwe}\n\n{f.Rationale}" +
                       (f.Suggestion is { } s ? $"\n\nSuggestion: {s}" : "");
            comments.Add(new InlineComment(f.File, f.Line.Value, "RIGHT", position.Value, body));
        }
        return comments;
    }

    /// <summary>
    /// Position of <paramref name="line"/> in the file's diff, per GitHub REST
    /// semantics: the line just below the first <c>@@</c> header is position 1
    /// and the count keeps increasing through every later line (context,
    /// additions, deletions and further <c>@@</c> headers) until the next file.
    /// Returns null when the line is not a +/context line of any hunk.
    /// </summary>
    internal static int? PositionInPatch(string patch, int line)
    {
        var state = new PatchCursor();
        foreach (var l in patch.Split('\n'))
        {
            if (l.StartsWith("@@", StringComparison.Ordinal))
            {
                state.StartHunk(l);
                continue;
            }
            var hit = state.Advance(l, line);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Walking state for <see cref="PositionInPatch"/>.</summary>
    private sealed class PatchCursor
    {
        private int _newLine;
        private int _position;

        public void StartHunk(string header)
        {
            // @@ -a,b +c,d @@ — header itself counts as a position once the
            // first hunk has started (first header is the position-0 anchor).
            if (_position > 0 || _newLine > 0) _position++;
            var plus = header.IndexOf('+');
            var comma = header.IndexOf(',', plus);
            var end = comma > 0 ? comma : header.IndexOf(' ', plus);
            _newLine = int.TryParse(header.AsSpan(plus + 1, end - plus - 1), out var n) ? n : 0;
        }

        public int? Advance(string l, int line)
        {
            if (_newLine == 0) return null;
            _position++;
            if (l.StartsWith('-')) return null;
            if (!l.StartsWith('+') && !l.StartsWith(' ')) return null;
            var hit = _newLine == line ? _position : (int?)null;
            _newLine++;
            return hit;
        }
    }
}
