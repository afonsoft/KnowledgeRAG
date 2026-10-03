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
                if (TryFlushFrame(data, out var parsed))
                    yield return (eventType, parsed);
                eventType = "message";
                continue;
            }
            if (line[0] == ':') continue; // heartbeat comment

            var (field, value) = SplitField(line);
            switch (field)
            {
                case "event": eventType = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0 && TryFlushFrame(data, out var last))
            yield return (eventType, last);
    }

    /// <summary>Empties <paramref name="data"/> and parses it as a JSON frame,
    /// quoting it as a plain string when it isn't valid JSON.</summary>
    private static bool TryFlushFrame(StringBuilder data, out JsonElement parsed)
    {
        parsed = default;
        if (data.Length == 0)
            return false;
        var text = data.ToString();
        data.Clear();
        try { parsed = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException)
        {
            parsed = JsonDocument.Parse($"\"{JsonEncodedText.Encode(text)}\"").RootElement.Clone();
        }
        return true;
    }

    private static (string Field, string Value) SplitField(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? (line, "") : (line[..colon], line[(colon + 1)..].TrimStart(' '));
    }
}
