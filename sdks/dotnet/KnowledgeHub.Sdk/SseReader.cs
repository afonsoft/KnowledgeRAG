using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KnowledgeHub.Sdk;

/// <summary>Minimal SSE frame reader for the hub's <c>/api/*/stream</c> endpoints.</summary>
internal static class SseReader
{
    /// <summary>Yields (eventType, dataJson) pairs from an SSE stream. Comment
    /// lines (<c>: keep-alive</c>) and multi-line data are handled per spec.</summary>
    internal static async IAsyncEnumerable<(string Event, JsonElement Data)> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var eventType = "message";
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var text = data.ToString();
                    data.Clear();
                    JsonElement parsed;
                    try { parsed = JsonDocument.Parse(text).RootElement.Clone(); }
                    catch (JsonException) { parsed = JsonDocument.Parse($"\"{JsonEncodedText.Encode(text)}\"").RootElement.Clone(); }
                    yield return (eventType, parsed);
                }
                eventType = "message";
                continue;
            }
            if (line[0] == ':') continue; // heartbeat comment

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');

            switch (field)
            {
                case "event": eventType = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0)
        {
            JsonElement parsed;
            try { parsed = JsonDocument.Parse(data.ToString()).RootElement.Clone(); }
            catch (JsonException) { yield break; }
            yield return (eventType, parsed);
        }
    }
}
