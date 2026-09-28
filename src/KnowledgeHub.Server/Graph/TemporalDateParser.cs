using System.Globalization;

namespace KnowledgeHub.Server.Graph;

/// <summary>
/// Tolerant timestamp/window parsing for temporal graph search
/// (SPEC-20260927-temporal-episodic-knowledge-graph RF-002/RF-003). Accepts
/// RFC3339/ISO-8601, <c>yyyy-MM-ddTHH:mm:ss</c>, <c>yyyy-MM-dd HH:mm:ss</c>
/// and bare <c>yyyy-MM-dd</c>; timezone-less inputs are interpreted as UTC.
/// Sliding windows are restricted to a fixed whitelist.
/// </summary>
public static class TemporalDateParser
{
    private static readonly (string Window, TimeSpan Span)[] Windows =
    [
        ("1h", TimeSpan.FromHours(1)),
        ("6h", TimeSpan.FromHours(6)),
        ("24h", TimeSpan.FromHours(24)),
        ("7d", TimeSpan.FromDays(7))
    ];

    /// <summary>Whitelisted sliding-window tokens (RF-003).</summary>
    public static IReadOnlyList<string> SupportedWindows { get; } =
        Windows.Select(w => w.Window).ToArray();

    private static readonly string[] ExactFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd"
    ];

    private const DateTimeStyles UtcStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>Parses a timestamp; timezone-less values are UTC.</summary>
    public static bool TryParse(string? input, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        input = input.Trim();

        if (DateTimeOffset.TryParseExact(
                input, ExactFormats, CultureInfo.InvariantCulture, UtcStyles, out var dto)
            || DateTimeOffset.TryParse(
                input, CultureInfo.InvariantCulture, UtcStyles, out dto))
        {
            value = dto.UtcDateTime;
            return true;
        }
        return false;
    }

    /// <summary>Throwing variant — the message names the accepted formats.</summary>
    public static DateTime Parse(string input, string paramName = "date") =>
        TryParse(input, out var value)
            ? value
            : throw new ArgumentException(
                $"invalid {paramName} '{input}' — expected RFC3339/ISO-8601, " +
                "yyyy-MM-ddTHH:mm:ss, yyyy-MM-dd HH:mm:ss or yyyy-MM-dd (UTC assumed)",
                paramName);

    /// <summary>Validates a sliding-window token against the whitelist.</summary>
    public static bool TryParseWindow(string? window, out TimeSpan span)
    {
        span = default;
        if (window is null)
            return false;
        foreach (var (token, s) in Windows)
            if (string.Equals(token, window.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                span = s;
                return true;
            }
        return false;
    }

    /// <summary>Throwing variant — the error lists every permitted value.</summary>
    public static TimeSpan ParseWindow(string window) =>
        TryParseWindow(window, out var span)
            ? span
            : throw new ArgumentException(
                $"invalid window '{window}' — permitted values: " +
                string.Join(", ", SupportedWindows),
                nameof(window));
}
