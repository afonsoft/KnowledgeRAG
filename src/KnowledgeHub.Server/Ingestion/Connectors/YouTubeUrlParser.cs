using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// YouTube URL/ID classifier (SPEC-20260927-youtube-transcript-connector RF-002):
/// accepts watch URLs, youtu.be, shorts, live, playlist, channel (UC…) and
/// @handle — returns a typed <see cref="YouTubeEntry"/> or null when the
/// input is unrecognizable.
/// </summary>
public enum YouTubeEntryKind { Video, Playlist, Channel }

/// <summary>One parsed YouTube entry from the <c>urls</c> configuration list.</summary>
public sealed record YouTubeEntry(YouTubeEntryKind Kind, string Id);

/// <summary>Classifies YouTube URLs and raw IDs into <see cref="YouTubeEntry"/>.</summary>
public static partial class YouTubeUrlParser
{
    private const int VideoIdLength = 11;

    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{11})", RegexOptions.CultureInvariant)]
    private static partial Regex WatchVParamRegex();

    /// <summary>Parses a single URL or raw ID. Returns null when unrecognizable.</summary>
    public static YouTubeEntry? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        return ParseRawId(trimmed) ?? ParseUrl(trimmed);
    }

    /// <summary>Raw identifier forms: video/playlist/channel IDs and @handles.</summary>
    private static YouTubeEntry? ParseRawId(string trimmed)
    {
        // Raw video ID (exactly 11 chars, alphanumeric + - and _)
        if (IsLikelyVideoId(trimmed))
            return new YouTubeEntry(YouTubeEntryKind.Video, trimmed);

        // Raw playlist ID (starts with PL, UU, OL, LL, FL, RD)
        if (IsLikelyPlaylistId(trimmed))
            return new YouTubeEntry(YouTubeEntryKind.Playlist, trimmed);

        // Raw channel ID (starts with UC, 24 chars)
        if (IsLikelyChannelId(trimmed))
            return new YouTubeEntry(YouTubeEntryKind.Channel, trimmed);

        // Raw @handle
        if (trimmed.StartsWith('@'))
            return new YouTubeEntry(YouTubeEntryKind.Channel, trimmed);

        return null;
    }

    /// <summary>URL forms across the youtube.com/youtu.be hosts.</summary>
    private static YouTubeEntry? ParseUrl(string trimmed)
    {
        // Must be a URL from here on
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("www.youtube.com" or "youtube.com" or "m.youtube.com" or "youtu.be"))
            return null;

        var path = uri.AbsolutePath.TrimStart('/');
        return host == "youtu.be" ? ParseShortHost(path) : ParseYouTubePath(uri, path);
    }

    /// <summary>youtu.be/{videoId} short-host form.</summary>
    private static YouTubeEntry? ParseShortHost(string path)
    {
        var id = path;
        if (IsLikelyVideoId(id))
            return new YouTubeEntry(YouTubeEntryKind.Video, id);
        return null;
    }

    /// <summary>youtube.com path dispatch: watch / shorts / live / playlist /
    /// channel / @handle.</summary>
    private static YouTubeEntry? ParseYouTubePath(Uri uri, string path)
    {
        // youtube.com/watch?v={videoId}
        if (path is "watch" or "watch/")
            return ParseWatchQuery(uri);

        // youtube.com/shorts/{videoId} and /live/{videoId}
        if (path.StartsWith("shorts/") || path.StartsWith("live/"))
        {
            var id = path[(path.IndexOf('/') + 1)..];
            if (IsLikelyVideoId(id))
                return new YouTubeEntry(YouTubeEntryKind.Video, id);
            return null;
        }

        // youtube.com/playlist?list={playlistId}
        if (path is "playlist" or "playlist/")
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var list = query["list"];
            if (!string.IsNullOrWhiteSpace(list))
                return new YouTubeEntry(YouTubeEntryKind.Playlist, list);
            return null;
        }

        // youtube.com/channel/{channelId}
        if (path.StartsWith("channel/"))
        {
            var id = path["channel/".Length..];
            if (!string.IsNullOrWhiteSpace(id))
                return new YouTubeEntry(YouTubeEntryKind.Channel, id);
            return null;
        }

        // youtube.com/@handle
        if (path.StartsWith('@'))
            return new YouTubeEntry(YouTubeEntryKind.Channel, path);

        return null;
    }

    /// <summary>watch?v={videoId} — checks the query with and without the leading ?.</summary>
    private static YouTubeEntry? ParseWatchQuery(Uri uri)
    {
        var match = WatchVParamRegex().Match(uri.Query.TrimStart('?'));
        if (match.Success)
            return new YouTubeEntry(YouTubeEntryKind.Video, match.Groups[1].Value);
        // Also check the full query string without the leading ?
        match = WatchVParamRegex().Match(uri.Query);
        if (match.Success)
            return new YouTubeEntry(YouTubeEntryKind.Video, match.Groups[1].Value);
        return null;
    }

    private static bool IsLikelyVideoId(string s) =>
        s.Length == VideoIdLength
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsLikelyPlaylistId(string s) =>
        s.Length >= 2
        && (s.StartsWith("PL") || s.StartsWith("UU") || s.StartsWith("OL")
            || s.StartsWith("LL") || s.StartsWith("FL") || s.StartsWith("RD"));

    private static bool IsLikelyChannelId(string s) =>
        s.StartsWith("UC") && s.Length >= 20;
}
