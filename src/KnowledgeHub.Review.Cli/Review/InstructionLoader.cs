namespace KnowledgeHub.Review.Review;

/// <summary>
/// Loads review instructions the way Devin Review does: <c>REVIEW.md</c> wins
/// (and may override <c>REVIEW_LANGUAGE</c> via a <c>language:</c> front line),
/// then <c>AGENTS.md</c> and <c>CLAUDE.md</c> conventions.
/// </summary>
public static class InstructionLoader
{
    public sealed record Instructions(string? ReviewMd, string? AgentsMd, string? ClaudeMd, string? LanguageOverride);

    public static Instructions Load(string repoRoot)
    {
        string? review = ReadFile(Path.Combine(repoRoot, "REVIEW.md"));
        string? agents = ReadFile(Path.Combine(repoRoot, "AGENTS.md"));
        string? claude = ReadFile(Path.Combine(repoRoot, "CLAUDE.md"));

        var lang = review?.Split('\n', StringSplitOptions.TrimEntries).Take(10)
            .Where(line => line.StartsWith("language:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["language:".Length..].Trim())
            .FirstOrDefault();
        return new Instructions(review, agents, claude, lang);
    }

    private static string? ReadFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
