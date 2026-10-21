using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace KnowledgeHub.Server.Context;

/// <summary>
/// Normalized repository scope <c>owner/name</c> (SPEC-20261004-context-
/// snapshots RF-001): trims, lowercases, accepts bare <c>owner/name</c> or a
/// full GitHub URL (<c>https://github.com/{owner}/{name}[.git]</c>).
/// </summary>
public sealed record RepoScope(string Owner, string Name)
{
    /// <summary>Canonical key — comparisons and storage always use it.</summary>
    public string Key => $"{Owner}/{Name}";

    private const int MaxSegmentLength = 100;

    private static readonly Regex Segment = new(
        @"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex GitHubUrl = new(
        @"^https?://(?:www\.)?github\.com/(?<owner>[A-Za-z0-9._-]{1,100})/(?<name>[A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static RepoScope Parse(string? raw)
    {
        if (!TryParse(raw, out var scope))
            throw new McpProtocolException("invalid_repo: expected 'owner/name' or a github.com repository URL",
                McpErrorCode.InvalidParams);
        return scope;
    }

    public static bool TryParse(string? raw, out RepoScope scope)
    {
        scope = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        var urlMatch = GitHubUrl.Match(value);
        if (urlMatch.Success)
        {
            scope = new RepoScope(
                urlMatch.Groups["owner"].Value.ToLowerInvariant(),
                urlMatch.Groups["name"].Value.ToLowerInvariant());
            return true;
        }

        var parts = value.Split('/', StringSplitOptions.None);
        if (parts is not [var owner, var name]
            || !Segment.IsMatch(owner) || !Segment.IsMatch(name))
            return false;

        scope = new RepoScope(owner.ToLowerInvariant(), name.ToLowerInvariant());
        return true;
    }
}
