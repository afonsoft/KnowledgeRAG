using System.Text.Json;

namespace KnowledgeHub.Shared;

public static class HttpResponseMessageExtensions
{
    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> but surfaces the server's
    /// error body — an <c>{ "error": "..." }</c> payload or a ProblemDetails <c>detail</c>/<c>title</c> —
    /// instead of the raw HTTP reason phrase, so callers can show a meaningful message.
    /// </summary>
    public static async Task EnsureSuccessOrApiErrorAsync(this HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        string? message = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    message = error.GetString();
                else if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    message = detail.GetString();
                else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    message = title.GetString();
            }
        }
        catch (Exception)
        {
            // Body unreadable or not JSON — fall back to the raw status line.
        }

        message ??= $"Response status code does not indicate success: {(int)status} ({response.ReasonPhrase}).";
        throw new HttpRequestException(message, inner: null, statusCode: status);
    }
}
