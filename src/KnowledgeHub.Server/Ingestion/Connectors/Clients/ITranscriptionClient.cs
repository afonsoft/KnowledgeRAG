namespace KnowledgeHub.Server.Ingestion.Connectors.Clients;

/// <summary>One diarized utterance from a transcription provider.</summary>
public sealed record TranscriptUtterance(string Speaker, long StartMs, long EndMs, string Text);

/// <summary>Auto-generated chapter boundary (AssemblyAI <c>auto_chapters</c>).</summary>
public sealed record TranscriptChapter(long StartMs, string Headline, string? Summary);

/// <summary>Normalized transcription output independent of provider shape.</summary>
public sealed record TranscriptionResult(
    IReadOnlyList<TranscriptUtterance> Utterances,
    IReadOnlyList<TranscriptChapter> Chapters,
    string PlainText,
    double? DurationSeconds);

/// <summary>
/// External transcription service (SPEC-20260927-audio-transcription-connector).
/// Implementations own upload + async job polling; no local audio decoding.
/// </summary>
public interface ITranscriptionClient
{
    Task<TranscriptionResult> TranscribeAsync(
        string filePath, string? language, bool diarization, bool chapters,
        string? apiKey, CancellationToken ct);
}
