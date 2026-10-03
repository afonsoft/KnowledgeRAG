using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KnowledgeHub.Shared;

namespace KnowledgeHub.Client.Services;

/// <summary>
/// SSE consumer for /api/ask/stream and /api/agent/stream
/// (SPEC-20260914-streaming-answers RF-002). Yields (event, data) pairs;
/// the caller decides how to render and falls back to the sync endpoint
/// when the connection fails before the first event.
/// </summary>
public sealed class StreamingApiClient(HttpClient http)
{
    public async IAsyncEnumerable<SseMessage> StreamAsync(
        string path, object payload, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            // SharedJson.Options: source-gen metadata for registered contract
            // types — reflection isn't available in trimmed WASM builds.
            Content = JsonContent.Create(payload, options: SharedJson.Options)
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? type = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                type = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal) && type is not null)
            {
                var json = JsonDocument.Parse(line[5..].Trim()).RootElement.Clone();
                yield return new SseMessage(type, json);
                type = null;
            }
        }
    }

    public sealed record SseMessage(string Event, JsonElement Data);
}
