using System.Security.Cryptography;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors.Clients;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// <see cref="SourceType.AudioTranscription"/> connector (SPEC-20260927
/// audio-transcription-connector). Local audio/video files → external
/// transcription (AssemblyAI default, OpenAI whisper-compatible endpoint
/// via <c>provider: whisper</c>) → diarized, chaptered Markdown.
/// <list type="bullet">
///   <item>RF-001: async job upload + bounded polling (AssemblyAI) or a
///   single multipart call (whisper-compatible).</item>
///   <item>RF-002: <see cref="AudioTranscriptionRenderer"/> emits chapters +
///   <c>[Speaker X - HH:MM:SS]</c> blocks, merging same-speaker turns &lt;10s.</item>
///   <item>RF-003: <c>audio:{sha256}:{provider}</c> fingerprint — unchanged
///   files emit stubs and never reach the paid API.</item>
/// </list>
/// API keys live in <see cref="IIntegrationSecretStore"/> under
/// <c>audio:{sourceId}</c>.
/// </summary>
public sealed class AudioTranscriptionConnector(
    IHttpClientFactory httpFactory,
    IIntegrationSecretStore secrets,
    ILogger<AudioTranscriptionConnector> logger)
    : ISourceConnector, IIncrementalSourceConnector
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".mp4", ".aac", ".flac", ".ogg", ".webm"
    };

    public SourceType Type => SourceType.AudioTranscription;

    internal static string SecretKey(Guid sourceId) => $"audio:{sourceId}";

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var provider = config.String("provider") is { Length: > 0 } p ? p.ToLowerInvariant() : "assemblyai";
        if (provider is not ("assemblyai" or "whisper"))
            throw new InvalidOperationException(
                $"audio: unsupported provider '{provider}' — assemblyai|whisper");

        var timeoutMinutes = config.Int("timeoutMinutes", 10, 1, 120);
        ITranscriptionClient client = provider == "whisper"
            ? new WhisperApiClient(httpFactory, config.String("endpoint") ?? "https://api.openai.com")
            : new AssemblyAiClient(httpFactory, "https://api.assemblyai.com", null,
                TimeSpan.FromMinutes(timeoutMinutes));
        var apiKey = await secrets.GetAsync(SecretKey(source.Id), cancellationToken);
        var language = config.String("language");
        var diarization = config.Bool("enableSpeakerDiarization", fallback: true);
        var chapters = config.Bool("enableAutoChapters", fallback: true);

        var files = EnumerateFiles(config).ToList();
        var warnings = new List<string>();
        var failed = new List<string>();
        var documents = new List<RawDocument>();
        var apiFailures = 0;

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var uri = $"file://{Path.GetFullPath(path)}";

            try
            {
                // RF-003: sha256 fingerprint — unchanged audio never reaches
                // the paid API.
                string fingerprint;
                await using (var fs = File.OpenRead(path))
                {
                    var sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken));
                    fingerprint = $"audio:{sha}:{provider}";
                }
                if (existingFingerprints.TryGetValue(uri, out var prev) && prev == fingerprint)
                {
                    documents.Add(new RawDocument(uri, "", "", prev));
                    continue;
                }

                var result = await client.TranscribeAsync(
                    path, language, diarization, chapters, apiKey, cancellationToken);
                // Cost guardrail (SPEC §8): log minutes of processed audio.
                if (result.DurationSeconds is { } secs)
                    logger.LogInformation("audio {File}: {Minutes:N1} min processed via {Provider}",
                        fileName, secs / 60, provider);
                documents.Add(new RawDocument(
                    uri, fileName,
                    AudioTranscriptionRenderer.Render(fileName, result), fingerprint));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                apiFailures++;
                warnings.Add($"{fileName}: {ex.Message}");
                failed.Add(uri);
            }
        }

        if (documents.Count == 0 && apiFailures > 0)
            throw new InvalidOperationException(
                $"audio: all {apiFailures} file(s) failed via {provider} — check the apiKey/endpoint and retry");

        return new FetchResult(documents, warnings,
            FailedUris: failed.Count > 0 ? failed : null);
    }

    /// <summary>folderPath (non-recursive, supported extensions) + explicit files.</summary>
    private static IEnumerable<string> EnumerateFiles(ConnectorConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = config.String("folderPath");
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFiles(folder))
                if (SupportedExtensions.Contains(Path.GetExtension(f)) && seen.Add(f))
                    yield return f;
        foreach (var f in config.StringArray("files"))
            if (SupportedExtensions.Contains(Path.GetExtension(f)) && File.Exists(f) && seen.Add(f))
                yield return f;
    }
}
