using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// Converts a caption track into readable text (SPEC-20260927-youtube-
/// transcript-connector RF-006): strips HTML tags and entities, deduplicates
/// consecutive repeated lines (common in auto-generated captions), and
/// formats with or without timestamps. The document header includes the
/// video title, channel, URL, and duration.
/// </summary>
public static partial class TranscriptRenderer
{
    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    /// <summary>Renders captions to a full document with header and body.</summary>
    public static string Render(VideoInfo video, IReadOnlyList<Caption> captions, bool includeTimestamps)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {video.Title}");
        sb.AppendLine($"Canal: {video.Author}");
        sb.AppendLine($"URL: https://youtu.be/{video.Id}");
        sb.AppendLine($"Duração: {FormatDuration(video.Duration)}");
        sb.AppendLine();

        string? lastNormalized = null;
        foreach (var caption in captions)
        {
            var text = StripHtml(caption.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var normalized = text.Trim();
            if (string.Equals(normalized, lastNormalized, StringComparison.OrdinalIgnoreCase))
                continue;
            lastNormalized = normalized;

            if (includeTimestamps)
                sb.AppendLine($"[{FormatDuration(caption.Offset)}] {normalized}");
            else
                sb.AppendLine(normalized);
        }

        return sb.ToString();
    }

    private static string StripHtml(string input)
    {
        var withoutTags = HtmlTagRegex().Replace(input, "");
        return System.Net.WebUtility.HtmlDecode(withoutTags);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var total = (long)duration.TotalSeconds;
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? $"{h:D2}:{m:D2}:{s:D2}"
            : $"{m:D2}:{s:D2}";
    }
}
