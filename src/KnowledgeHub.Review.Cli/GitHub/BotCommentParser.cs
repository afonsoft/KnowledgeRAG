using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.GitHub;

/// <summary>
/// Filters review/issue comments down to known bot authors: any login ending
/// in <c>[bot]</c>, the built-in default list, plus the <c>REVIEW_BOT_AUTHORS</c>
/// allowlist (RF-001).
/// </summary>
public static class BotCommentParser
{
    /// <summary>True when <paramref name="author"/> is a bot per suffix or allowlist.</summary>
    public static bool IsBot(string author, ReviewOptions options)
    {
        if (author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
            return true;
        var normalized = author.Replace("[bot]", "", StringComparison.OrdinalIgnoreCase);
        return DefaultBot(options).Any(b =>
            string.Equals(b, author, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(b, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<BotCommentSignal> Filter(IEnumerable<BotCommentSignal> comments, ReviewOptions options) =>
        comments.Where(c => IsBot(c.Author, options)).ToList();

    private static IEnumerable<string> DefaultBot(ReviewOptions options) =>
        ReviewOptions.DefaultBotAuthors.Concat(options.BotAuthors);
}
