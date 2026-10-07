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
        var newLine = 0;
        var position = 0;
        foreach (var l in patch.Split('\n'))
        {
            if (l.StartsWith("@@", StringComparison.Ordinal))
            {
                // @@ -a,b +c,d @@ — header itself counts as a position once the
                // first hunk has started (first header is the position-0 anchor).
                if (position > 0 || newLine > 0) position++;
                var plus = l.IndexOf('+');
                var comma = l.IndexOf(',', plus);
                var end = comma > 0 ? comma : l.IndexOf(' ', plus);
                newLine = int.TryParse(l.AsSpan(plus + 1, end - plus - 1), out var n) ? n : 0;
                continue;
            }
            if (newLine == 0) continue;
            position++;
            if (l.StartsWith('-')) continue;
            if (l.StartsWith('+') || l.StartsWith(' '))
            {
                if (newLine == line) return position;
                newLine++;
            }
        }
        return null;
    }
}
