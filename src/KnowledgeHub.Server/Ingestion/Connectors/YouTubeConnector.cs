using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// YouTube transcript connector (SPEC-20260927-youtube-transcript-connector
/// RF-004/RF-005/RF-007): discovers videos from URLs/IDs (single video,
/// playlist, channel), fetches closed captions via YoutubeExplode, renders
/// transcript text, and maps each video to a <see cref="RawDocument"/>.
/// Incremental sync by <c>yt:{videoId}</c> fingerprint — unchanged videos
/// emit stubs without calling the caption API.
/// </summary>
public sealed partial class YouTubeConnector(
    IYouTubeClient youTube,
    ILogger<YouTubeConnector> logger) : IIncrementalSourceConnector, IItemFetchConnector
{
    public SourceType Type => SourceType.YouTube;

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var urls = config.StringArray("urls");
        var language = config.String("language");
        var includeAutoCaptions = config.Bool("includeAutoCaptions", true);
        var includeTimestamps = config.Bool("includeTimestamps", false);
        var maxVideos = config.Int("maxVideos", 100, 1, 500);
        var forceRefresh = config.Bool("forceRefresh");

        var warnings = new List<string>();
        var failedUris = new List<string>();
        var seenVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<RawDocument>();
        var truncated = false;

        await foreach (var video in EnumerateVideosAsync(urls, warnings, cancellationToken))
        {
            if (documents.Count >= maxVideos)
            {
                truncated = true;
                warnings.Add($"truncated at maxVideos={maxVideos} ({documents.Count} videos indexed)");
                break;
            }

            if (!seenVideoIds.Add(video.Id))
                continue; // dedup

            var uriRef = $"yt://video/{video.Id}";
            var fingerprint = $"yt:{video.Id}";

            // Incremental: skip if fingerprint matches (unless forceRefresh)
            if (!forceRefresh
                && existingFingerprints.TryGetValue(uriRef, out var stored)
                && stored == fingerprint)
            {
                documents.Add(new RawDocument(uriRef, video.Title, "", fingerprint));
                continue;
            }

            try
            {
                var captions = await FetchCaptionsAsync(video.Id, language, includeAutoCaptions, cancellationToken);
                if (captions is null || captions.Count == 0)
                {
                    warnings.Add($"vídeo sem legendas: '{video.Title}' (id={video.Id})");
                    failedUris.Add(uriRef);
                    continue;
                }

                var text = TranscriptRenderer.Render(video, captions, includeTimestamps);
                documents.Add(new RawDocument(uriRef, video.Title, text, fingerprint));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "YouTube video {VideoId} failed during caption fetch", video.Id);
                warnings.Add($"vídeo '{video.Title}' (id={video.Id}) falhou: {ex.Message}");
                failedUris.Add(uriRef);
            }
        }

        return new FetchResult(documents, warnings,
            failedUris.Count > 0 ? failedUris : null, truncated);
    }

    public async Task<RawDocument?> FetchItemAsync(
        KnowledgeSource source, string uriReference, CancellationToken cancellationToken)
    {
        const string prefix = "yt://video/";
        if (!uriReference.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var videoId = uriReference[prefix.Length..];

        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var language = config.String("language");
        var includeAutoCaptions = config.Bool("includeAutoCaptions", true);
        var includeTimestamps = config.Bool("includeTimestamps", false);

        try
        {
            var video = await youTube.GetVideoAsync(videoId, cancellationToken);
            var captions = await FetchCaptionsAsync(videoId, language, includeAutoCaptions, cancellationToken);
            if (captions is null || captions.Count == 0)
                return null;
            var text = TranscriptRenderer.Render(video, captions, includeTimestamps);
            return new RawDocument(uriReference, video.Title, text, $"yt:{videoId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "YouTube FetchItemAsync for {VideoId} failed", videoId);
            return null;
        }
    }

    /// <summary>Enumerates all videos from all parsed entries (RF-004).</summary>
    private async IAsyncEnumerable<VideoInfo> EnumerateVideosAsync(
        string[] urls, List<string> warnings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var url in urls)
        {
            var entry = YouTubeUrlParser.Parse(url);
            if (entry is null)
            {
                warnings.Add($"URL não reconhecida: '{url.Trim()}'");
                continue;
            }

            IAsyncEnumerable<VideoInfo> source;
            try
            {
                source = entry.Kind switch
                {
                    YouTubeEntryKind.Video => SingleVideo(entry.Id),
                    YouTubeEntryKind.Playlist => youTube.GetPlaylistVideosAsync(entry.Id, ct),
                    YouTubeEntryKind.Channel => youTube.GetChannelUploadsAsync(entry.Id, ct),
                    _ => throw new InvalidOperationException()
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"falha ao expandir '{url.Trim()}': {ex.Message}");
                continue;
            }

            await foreach (var video in source.WithCancellation(ct))
                yield return video;
        }
    }

    private async IAsyncEnumerable<VideoInfo> SingleVideo(string videoId)
    {
        var video = await youTube.GetVideoAsync(videoId, CancellationToken.None);
        yield return video;
    }

    /// <summary>Selects the best caption track (RF-005) and fetches captions.</summary>
    private async Task<IReadOnlyList<Caption>?> FetchCaptionsAsync(
        string videoId, string? language, bool includeAutoCaptions, CancellationToken ct)
    {
        var manifest = await youTube.GetCaptionManifestAsync(videoId, ct);
        if (manifest.Count == 0)
            return null;

        var track = SelectTrack(manifest, language, includeAutoCaptions);
        if (track is null)
            return null;

        return await youTube.GetCaptionTrackAsync(track, ct);
    }

    /// <summary>Track selection priority (RF-005): (1) manual + language match,
    /// (2) any manual, (3) auto + language match (if includeAutoCaptions),
    /// (4) any auto (if includeAutoCaptions), (5) null.</summary>
    private static CaptionTrackInfo? SelectTrack(
        IReadOnlyList<CaptionTrackInfo> manifest, string? language, bool includeAutoCaptions)
    {
        var langPrefix = !string.IsNullOrWhiteSpace(language)
            ? language.Split('-')[0].ToLowerInvariant()
            : null;

        var manual = manifest.Where(t => !t.IsAutoGenerated).ToList();
        var auto = manifest.Where(t => t.IsAutoGenerated).ToList();

        // (1) Manual with language match
        if (langPrefix is not null)
        {
            var match = manual.FirstOrDefault(t => LanguageMatches(t.Language, langPrefix));
            if (match is not null) return match;
        }
        // (2) Any manual
        if (manual.Count > 0) return manual[0];
        // (3) Auto with language match
        if (includeAutoCaptions && langPrefix is not null)
        {
            var match = auto.FirstOrDefault(t => LanguageMatches(t.Language, langPrefix));
            if (match is not null) return match;
        }
        // (4) Any auto
        if (includeAutoCaptions && auto.Count > 0) return auto[0];
        // (5) None
        return null;
    }

    /// <summary>Language match by prefix (pt matches pt-BR, pt-PT).</summary>
    private static bool LanguageMatches(string trackLanguage, string langPrefix)
    {
        var trackPrefix = trackLanguage.Split('-')[0].ToLowerInvariant();
        return string.Equals(trackPrefix, langPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
