using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>Error from the Notion REST API. Carries the HTTP status so callers
/// can distinguish auth failures (abort sync) from per-item failures (warning).</summary>
public sealed class NotionApiException : Exception
{
    public NotionApiException(HttpStatusCode? statusCode, string message)
        : base(message) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Thin typed client over the Notion REST API (SPEC-20260919-notion-connector
/// RF-002): /users/me probe, /search, /blocks/{id}/children and
/// /databases/{id}/query with cursor pagination (page_size=100), a ≥350 ms
/// politeness throttle and Retry-After honoring on 429 (max 3 retries).
/// The token never reaches logs or exception messages.
/// </summary>
public sealed class NotionApiClient
{
    private const int MaxRetries = 3;
    private const int PageSize = 100;
    private static readonly TimeSpan DefaultThrottle = TimeSpan.FromMilliseconds(350);

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _apiVersion;
    private readonly TimeSpan _throttle;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public NotionApiClient(HttpClient http, string token, string apiVersion, TimeSpan? throttle = null)
    {
        _http = http;
        _token = token;
        _apiVersion = apiVersion;
        _throttle = throttle ?? DefaultThrottle;
    }

    /// <summary>GET /v1/users/me — validates the token before any real fetch.</summary>
    public async Task<JsonElement> ProbeAsync(CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, "v1/users/me", null, cancellationToken);

    /// <summary>GET /v1/pages/{id} — page metadata (title, last_edited_time) for
    /// root-traversal discovery, where /search is not consulted.</summary>
    public async Task<JsonElement> GetPageAsync(string pageId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, $"v1/pages/{Uri.EscapeDataString(pageId)}", null, cancellationToken);

    /// <summary>POST /v1/search paginated over everything shared with the
    /// integration (pages and databases — no object filter, one pass).</summary>
    public async IAsyncEnumerable<JsonElement> SearchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var body = new Dictionary<string, object?> { ["page_size"] = PageSize };
            if (cursor is not null)
                body["start_cursor"] = cursor;

            var page = await SendAsync(HttpMethod.Post, "v1/search", body, cancellationToken);
            foreach (var item in GetResults(page))
                yield return item;

            cursor = NextCursor(page);
        } while (cursor is not null);
    }

    /// <summary>GET /v1/blocks/{id}/children paginated.</summary>
    public async IAsyncEnumerable<JsonElement> GetBlockChildrenAsync(
        string blockId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var path = $"v1/blocks/{Uri.EscapeDataString(blockId)}/children?page_size={PageSize}";
            if (cursor is not null)
                path += $"&start_cursor={Uri.EscapeDataString(cursor)}";

            var page = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            foreach (var item in GetResults(page))
                yield return item;

            cursor = NextCursor(page);
        } while (cursor is not null);
    }

    /// <summary>POST /v1/databases/{id}/query paginated — each result is a page row.</summary>
    public async IAsyncEnumerable<JsonElement> QueryDatabaseAsync(
        string databaseId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var body = new Dictionary<string, object?> { ["page_size"] = PageSize };
            if (cursor is not null)
                body["start_cursor"] = cursor;

            var page = await SendAsync(
                HttpMethod.Post, $"v1/databases/{Uri.EscapeDataString(databaseId)}/query",
                body, cancellationToken);
            foreach (var item in GetResults(page))
                yield return item;

            cursor = NextCursor(page);
        } while (cursor is not null);
    }

    private static IEnumerable<JsonElement> GetResults(JsonElement page)
    {
        if (page.ValueKind == JsonValueKind.Object
            && page.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
                yield return item;
        }
    }

    private static string? NextCursor(JsonElement page) =>
        page.ValueKind == JsonValueKind.Object
        && page.TryGetProperty("has_more", out var more)
        && more.ValueKind == JsonValueKind.True
        && page.TryGetProperty("next_cursor", out var next)
        && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        var wait = _throttle - (DateTimeOffset.UtcNow - _lastRequest);
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellationToken);
        _lastRequest = DateTimeOffset.UtcNow;
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            await ThrottleAsync(cancellationToken);

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("Notion-Version", _apiVersion);
            if (body is not null)
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NotionApiException(null, "request timed out");
            }
            catch (HttpRequestException ex)
            {
                throw new NotionApiException(null, $"request failed: {ex.Message}");
            }

            using (response)
            {
                if (response.StatusCode == (HttpStatusCode)429 && attempt <= MaxRetries)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new NotionApiException(response.StatusCode, ParseError(text));

                return JsonDocument.Parse(text).RootElement.Clone();
            }
        }
    }

    /// <summary>Extracts <c>code</c>/<c>message</c> from Notion's error envelope.</summary>
    private static string ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            var message = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            if (code is not null && message is not null)
                return $"{code}: {message}";
            if (message is not null)
                return message;
        }
        catch (JsonException) { /* fall through to truncated raw body */ }
        return body.Length <= 200 ? body : body[..200];
    }
}
