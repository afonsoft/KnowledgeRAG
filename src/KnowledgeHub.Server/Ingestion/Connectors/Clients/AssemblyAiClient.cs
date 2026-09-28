using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeHub.Server.Ingestion.Connectors.Clients;

/// <summary>
/// AssemblyAI async transcription client (SPEC-20260927 RF-001):
/// <c>POST /v2/upload</c> → <c>POST /v2/transcript</c> → polling
/// <c>GET /v2/transcript/{id}</c> until <c>completed</c>/<c>error</c>.
/// Speaker diarization via <c>speaker_labels</c>, chapters via
/// <c>auto_chapters</c>. Auth is the <c>authorization</c> header (required).
/// </summary>
public sealed class AssemblyAiClient(
    IHttpClientFactory httpFactory,
    string baseUrl = "https://api.assemblyai.com",
    TimeSpan? pollInterval = null) : ITranscriptionClient
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _maxWait = TimeSpan.FromMinutes(10);

    public AssemblyAiClient(IHttpClientFactory httpFactory, string baseUrl,
        TimeSpan? pollInterval, TimeSpan maxWait)
        : this(httpFactory, baseUrl, pollInterval)
        => _maxWait = maxWait;

    public async Task<TranscriptionResult> TranscribeAsync(
        string filePath, string? language, bool diarization, bool chapters,
        string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "audio: AssemblyAI requires an apiKey — set it in the source secrets");

        var client = httpFactory.CreateClient("audio");
        using var uploadRequest = Request(HttpMethod.Post, $"{baseUrl}/v2/upload", apiKey);
        uploadRequest.Content = new StreamContent(File.OpenRead(filePath));
        using var uploadResponse = await client.SendAsync(uploadRequest, ct);
        if (uploadResponse.StatusCode == HttpStatusCode.BadRequest)
            throw new InvalidOperationException(
                $"audio: AssemblyAI rejected '{Path.GetFileName(filePath)}' (400 — invalid or corrupt media)");
        await EnsureOk(uploadResponse, "upload", ct);
        var uploadJson = JsonNode.Parse(await uploadResponse.Content.ReadAsStringAsync(ct));
        var audioUrl = uploadJson?["upload_url"]?.GetValue<string>()
            ?? throw new InvalidOperationException("audio: upload response missing upload_url");

        var payload = new JsonObject
        {
            ["audio_url"] = audioUrl,
            ["speaker_labels"] = diarization,
            ["auto_chapters"] = chapters
        };
        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            payload["language_code"] = language;

        using var submit = Request(HttpMethod.Post, $"{baseUrl}/v2/transcript", apiKey);
        submit.Content = JsonContent.Create(payload);
        using var submitted = await client.SendAsync(submit, ct);
        await EnsureOk(submitted, "transcript submit", ct);
        var jobJson = JsonNode.Parse(await submitted.Content.ReadAsStringAsync(ct));
        var id = jobJson?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("audio: transcript response missing id");

        // Exponential backoff poll: start at _pollInterval, double to 15s cap.
        var delay = _pollInterval;
        var deadline = DateTimeOffset.UtcNow + _maxWait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var poll = Request(HttpMethod.Get, $"{baseUrl}/v2/transcript/{id}", apiKey);
            using var polled = await client.SendAsync(poll, ct);
            await EnsureOk(polled, "transcript poll", ct);
            var job = JsonNode.Parse(await polled.Content.ReadAsStringAsync(ct));
            var status = job?["status"]?.GetValue<string>();
            switch (status)
            {
                case "completed":
                    return Parse(job!);
                case "error":
                    throw new InvalidOperationException(
                        $"audio: AssemblyAI job failed — {job?["error"]?.GetValue<string>() ?? "unknown"}");
                case null:
                    throw new InvalidOperationException("audio: poll response missing status");
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"audio: transcription timed out after {_maxWait.TotalMinutes:N0} minutes");
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(15).Ticks));
        }
    }

    private static TranscriptionResult Parse(JsonNode job)
    {
        var utterances = new List<TranscriptUtterance>();
        if (job["utterances"] is JsonArray utts)
            foreach (var u in utts)
                utterances.Add(new TranscriptUtterance(
                    u?["speaker"]?.GetValue<string>() ?? "A",
                    u?["start"]?.GetValue<long>() ?? 0,
                    u?["end"]?.GetValue<long>() ?? 0,
                    u?["text"]?.GetValue<string>() ?? ""));

        var chapters = new List<TranscriptChapter>();
        if (job["chapters"] is JsonArray ch)
            foreach (var c in ch)
                chapters.Add(new TranscriptChapter(
                    c?["start"]?.GetValue<long>() ?? 0,
                    c?["headline"]?.GetValue<string>() ?? "",
                    c?["summary"]?.GetValue<string>()));

        // No utterances (diarization off) → fall back to the flat text.
        if (utterances.Count == 0 && job["text"]?.GetValue<string>() is { Length: > 0 } flat)
            utterances.Add(new TranscriptUtterance("A", 0, 0, flat));

        return new TranscriptionResult(
            utterances, chapters,
            job["text"]?.GetValue<string>() ?? "",
            job["audio_duration"] is JsonValue d && d.TryGetValue<double>(out var secs) ? secs : null);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string apiKey)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("authorization", apiKey);
        return r;
    }

    private static async Task EnsureOk(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        _ = await response.Content.ReadAsStringAsync(ct); // drain for keep-alive reuse
        throw new InvalidOperationException(
            $"audio: AssemblyAI {action} failed ({(int)response.StatusCode}) — retry the sync later");
    }
}
