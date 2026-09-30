using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace KnowledgeHub.Server.Ingestion.Connectors.Clients;

/// <summary>
/// OpenAI Audio API (`POST /v1/audio/transcriptions`, multipart +
/// <c>response_format=verbose_json</c>) — also compatible with self-hosted
/// whisper endpoints via the configurable base URL. Whisper returns
/// timestamped <c>segments</c>; diarization/chapters are not supported by
/// the API and degrade to a single "Speaker A" stream.
/// </summary>
public sealed class WhisperApiClient(
    IHttpClientFactory httpFactory,
    string baseUrl = WhisperApiClient.DefaultBaseUrl) : ITranscriptionClient
{
    /// <summary>Hosted OpenAI-compatible Whisper endpoint.</summary>
    public const string DefaultBaseUrl = "https://api.openai.com";
    public async Task<TranscriptionResult> TranscribeAsync(
        string filePath, string? language, bool diarization, bool chapters,
        string? apiKey, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("audio");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/audio/transcriptions");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var form = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(filePath));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(filePath));
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            form.Add(new StringContent(language), "language");
        request.Content = form;

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _ = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"audio: whisper transcription failed ({(int)response.StatusCode})");
        }

        var job = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var utterances = new List<TranscriptUtterance>();
        if (job?["segments"] is JsonArray segs)
            foreach (var s in segs)
                utterances.Add(new TranscriptUtterance(
                    "A",
                    (long)((s?["start"]?.GetValue<double>() ?? 0) * 1000),
                    (long)((s?["end"]?.GetValue<double>() ?? 0) * 1000),
                    s?["text"]?.GetValue<string>() ?? ""));
        var duration = job?["duration"] is JsonValue d && d.TryGetValue<double>(out var secs) ? secs : (double?)null;
        return new TranscriptionResult(
            utterances, [], job?["text"]?.GetValue<string>() ?? "", duration);
    }
}
