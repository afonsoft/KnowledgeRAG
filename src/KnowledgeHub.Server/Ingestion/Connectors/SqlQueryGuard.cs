using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// Read-only SQL validation (SPEC-20260927-restapi-sqldatabase-connectors RF-004):
/// a query is only valid when it is a single statement that starts with
/// <c>SELECT</c> or <c>WITH</c> (case-insensitive), has no intermediate
/// semicolons and contains no write keywords as whole words outside string
/// literals, quoted identifiers or comments.
/// </summary>
public static partial class SqlQueryGuard
{
    private const string WriteKeywords =
        "INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|ATTACH|DETACH|PRAGMA|EXEC|EXECUTE|"
        + "GRANT|REVOKE|COPY|CALL|TRUNCATE|VACUUM|MERGE|REPLACE";

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(500);

    [GeneratedRegex(@"\b(?:INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|ATTACH|DETACH|PRAGMA|EXEC|EXECUTE|GRANT|REVOKE|COPY|CALL|TRUNCATE|VACUUM|MERGE|REPLACE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 500)]
    private static partial Regex WriteKeywordRegex();

    /// <summary>Keyword list surfaced for diagnostics and tests.</summary>
    public static IReadOnlyList<string> Keywords { get; } =
        WriteKeywords.Split('|');

    /// <summary>Returns (true, null) when the query is a safe read-only
    /// statement, otherwise (false, reason).</summary>
    public static (bool Ok, string? Reason) Validate(string? query)
    {
        // Blank comments/literals/identifiers so keywords inside them never count.
        var sanitized = BlankLiteralsAndComments(query).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            return (false, "query is empty");

        // First token must be SELECT or WITH.
        Match first;
        try
        {
            first = Regex.Match(
                sanitized, @"^[A-Za-z]+", RegexOptions.CultureInvariant, RegexMatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return (false, "query validation timed out");
        }
        if (!first.Success || first.Value.ToUpperInvariant() is not ("SELECT" or "WITH"))
            return (false, $"query must start with SELECT or WITH (found '{first.Value}')");

        // Single statement: at most one semicolon and only at the end.
        if (sanitized.EndsWith(';'))
            sanitized = sanitized[..^1].TrimEnd();
        if (sanitized.Contains(';'))
            return (false, "only a single statement is allowed (no intermediate ';')");

        Match match;
        try
        {
            match = WriteKeywordRegex().Match(sanitized);
        }
        catch (RegexMatchTimeoutException)
        {
            return (false, "query validation timed out");
        }
        if (match.Success)
            return (false, $"write keyword '{match.Value.ToUpperInvariant()}' is not allowed (read-only queries only)");

        return (true, null);
    }

    /// <summary>Single-pass scanner: replaces string literals ('…', '' escape),
    /// quoted identifiers ("…") and comments (--…, /* … */) with spaces while
    /// preserving the rest of the text.</summary>
    private static string BlankLiteralsAndComments(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        var sb = new StringBuilder(query.Length);
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            switch (c)
            {
                case '\'' when Next(i) != '\'':
                    i = SkipSingleQuoted(query, i);
                    sb.Append(' ');
                    break;
                case '\'' when Next(i) == '\'':
                    i += 2; // escaped quote inside literal — stays blanked
                    sb.Append(' ');
                    break;
                case '"':
                    i = SkipDoubleQuoted(query, i);
                    sb.Append(' ');
                    break;
                case '-' when Next(i) == '-':
                    i = SkipLineComment(query, i);
                    sb.Append(' ');
                    break;
                case '/' when Next(i) == '*':
                    i = SkipBlockComment(query, i);
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }
        return sb.ToString();

        char Next(int pos) => pos + 1 < query.Length ? query[pos + 1] : '\0';
    }

    private static int SkipSingleQuoted(string s, int start)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length; // unterminated literal — blank the rest
    }

    private static int SkipDoubleQuoted(string s, int start)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static int SkipLineComment(string s, int start)
    {
        var i = start + 2;
        while (i < s.Length && s[i] != '\n')
            i++;
        return i; // keep the newline
    }

    private static int SkipBlockComment(string s, int start)
    {
        var i = start + 2;
        while (i < s.Length && (s[i] != '*' || i + 1 >= s.Length || s[i + 1] != '/'))
            i++;
        return Math.Min(i + 2, s.Length);
    }
}
