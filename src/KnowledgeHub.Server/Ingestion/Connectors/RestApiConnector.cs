using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// RestApi connector (SPEC-20260927-restapi-sqldatabase-connectors RF-001/RF-002/RF-003):
/// GETs a JSON endpoint, extracts an item array via <c>itemsPath</c> (dot-path)
/// and maps each item to a <see cref="RawDocument"/> through the configured
/// field paths. Optional <c>pageParam</c> pagination repeats
/// <c>?{pageParam}=1..maxPages</c> until an empty page. Request headers live
/// in the encrypted store (<c>restapi:{sourceId}</c>) — never in the
/// persisted configuration. First-page failures fail the sync; later-page
/// failures warn and keep what was collected.
/// </summary>
public sealed class RestApiConnector(
    IHttpClientFactory httpClientFactory,
    IIntegrationSecretStore secrets,
    ILogger<RestApiConnector> logger) : ISourceConnector
{
    public SourceType Type => SourceType.RestApi;

    /// <summary>Secret-store key holding the request headers JSON object.</summary>
    public static string SecretKey(Guid sourceId) => $"restapi:{sourceId}";

    public async Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var endpoint = config.String("endpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"RestApi source '{source.Name}' has no valid 'endpoint'");

        var headers = await ResolveHeadersAsync(source, config, cancellationToken);
        var itemsPath = config.String("itemsPath");
        var titleField = config.String("titleField");
        var contentFields = config.StringArray("contentFields");
        var urlField = config.String("urlField");
        var idField = config.String("idField");
        var pageParam = config.String("pageParam");
        var maxPages = config.Int("maxPages", 1, 1, 50);

        var http = httpClientFactory.CreateClient("restapi");
        var (items, warnings, warned) = await FetchAllPagesAsync(
            source, http, endpoint, headers, itemsPath, pageParam, maxPages, cancellationToken);

        var documents = new List<RawDocument>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var doc = MapItem(items[i], i + 1,
                new ItemMapping(titleField, contentFields, urlField, idField), warnings, warned);
            if (doc is not null)
                documents.Add(doc);
        }

        if (documents.Count == 0 && items.Count > 0)
            logger.LogWarning("RestApi source {SourceId} mapped no documents from {ItemCount} items", source.Id, items.Count);

        return new FetchResult(documents, warnings);
    }

    /// <summary>Walks the pagination loop: page 1 failure aborts the sync,
    /// later failures keep collected items, empty page ends pagination.</summary>
    private async Task<(List<JsonElement> Items, List<string> Warnings, HashSet<string> Warned)>
        FetchAllPagesAsync(
            KnowledgeSource source, HttpClient http, string endpoint,
            IReadOnlyDictionary<string, string> headers, string? itemsPath,
            string? pageParam, int maxPages, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var warnings = new List<string>();
        var warned = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 1; page <= maxPages; page++)
        {
            var requestUrl = pageParam is null
                ? endpoint
                : PageUrl(endpoint, pageParam, page);

            JsonElement body;
            try
            {
                body = await GetJsonAsync(http, requestUrl, headers, page == 1, ct);
            }
            catch (RestApiFetchException ex)
            {
                if (page == 1)
                    throw new InvalidOperationException(
                        $"RestApi source '{source.Name}': {ex.Message}", ex);
                WarnOnce(warnings, warned, $"page {page} fetch failed ({ex.Message}) — keeping collected items");
                break;
            }

            var pageItems = SelectItems(body, itemsPath, warnings, warned);
            if (pageItems.Count == 0)
                break; // empty page ends pagination
            items.AddRange(pageItems);

            if (pageParam is null)
                break; // single-shot fetch
        }

        return (items, warnings, warned);
    }

    /// <summary>Headers live in the encrypted store; <c>hasKey:true</c> without a
    /// stored secret is a configuration error that can never sync.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(
        KnowledgeSource source, ConnectorConfig config, CancellationToken ct)
    {
        var hasKey = config.Bool("hasKey");
        var stored = await secrets.GetAsync(SecretKey(source.Id), ct);
        if (stored is null)
        {
            if (hasKey)
                throw new InvalidOperationException(
                    $"RestApi source '{source.Name}': headers marcados como configurados (hasKey) mas ausentes do secret store — salve a source novamente");
            return new Dictionary<string, string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(stored);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("not an object");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject()
                .Where(p => p.Name.Length > 0
                    && p.Value.ValueKind == JsonValueKind.String
                    && p.Value.GetString() is { Length: > 0 }))
                headers[property.Name] = property.Value.GetString()!;
            return headers;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"RestApi source '{source.Name}': headers armazenados não são um objeto JSON válido", ex);
        }
    }

    private static string PageUrl(string endpoint, string pageParam, int page)
    {
        var separator = endpoint.Contains('?') ? "&" : "?";
        return $"{endpoint}{separator}{Uri.EscapeDataString(pageParam)}={page}";
    }

    /// <summary>Raised for fetch failures that are safe to surface to the caller
    /// (HTTP status, non-JSON payload, retry exhaustion).</summary>
    public sealed class RestApiFetchException(string message) : Exception(message);

    /// <summary>Fetches and parses one page. The first page failing fails the sync
    /// (rethrown); 429 honors <c>Retry-After</c> once.</summary>
    private static async Task<JsonElement> GetJsonAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string> headers,
        bool firstPage, CancellationToken ct)
    {
        var response = await SendAsync(http, url, headers, ct);
        if ((int)response.StatusCode == 429)
        {
            var retryAfter = RetryAfterSeconds(response);
            response.Dispose();
            if (retryAfter is null)
                throw new RestApiFetchException("HTTP 429 sem Retry-After — desistindo após 1 tentativa");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(retryAfter.Value, 10_000)), ct);
            response = await SendAsync(http, url, headers, ct);
            if ((int)response.StatusCode == 429)
            {
                response.Dispose();
                throw new RestApiFetchException("HTTP 429 persistiu após honrar Retry-After (1 tentativa)");
            }
        }

        using var resp = response;
        if (!resp.IsSuccessStatusCode)
        {
            var message = (int)resp.StatusCode switch
            {
                401 or 403 => $"credenciais/endpoint inválidos (HTTP {(int)resp.StatusCode})",
                404 => $"endpoint não encontrado (HTTP 404)",
                _ => $"endpoint retornou HTTP {(int)resp.StatusCode}"
            };
            throw new RestApiFetchException(message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            if (!firstPage)
                throw new RestApiFetchException($"page returned a non-JSON body");
            throw new InvalidOperationException(
                $"RestApi: endpoint retornou um corpo não-JSON (content-type '{ContentTypeName(resp.Content)}') — apenas respostas JSON são suportadas");
        }
    }

    private static int? RetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw is not null && int.TryParse(raw, out var seconds) && seconds >= 0)
                return seconds * 1000;
        }
        return null;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string ContentTypeName(HttpContent? content) =>
        content?.Headers.ContentType?.MediaType ?? "unknown";

    /// <summary>Extracts the item array: <c>itemsPath</c> dot-path over the body;
    /// empty path → root array (or the whole body as a single item). A path that
    /// does not resolve warns once and falls back to the whole body.</summary>
    private static List<JsonElement> SelectItems(
        JsonElement body, string? itemsPath, List<string> warnings, HashSet<string> warned)
    {
        if (!string.IsNullOrWhiteSpace(itemsPath))
        {
            if (JsonPathResolver.Resolve(body, itemsPath) is { } selected)
            {
                if (selected.ValueKind == JsonValueKind.Array)
                    return selected.EnumerateArray().ToList();
                // SPEC §6 edge case: itemsPath points at a non-array → the object
                // itself becomes a single serialized document.
                return [selected];
            }
            WarnOnce(warnings, warned, $"itemsPath '{itemsPath}' not found — treating whole body as one item");
        }

        if (body.ValueKind == JsonValueKind.Array)
            return body.EnumerateArray().ToList();
        return [body];
    }

    /// <summary>Field-mapping configuration for <see cref="MapItem"/> (RF-002).</summary>
    private sealed record ItemMapping(
        string? TitleField, string[] ContentFields, string? UrlField, string? IdField);

    /// <summary>Maps one JSON item to a <see cref="RawDocument"/> (RF-002).</summary>
    private static RawDocument? MapItem(
        JsonElement item, int index, ItemMapping mapping, List<string> warnings, HashSet<string> warned)
    {
        var title = FieldOrNull(item, mapping.TitleField) ?? $"item {index}";

        var content = mapping.ContentFields.Length > 0
            ? JoinContentFields(item, mapping.ContentFields, warnings, warned)
            : DefaultSerialization(item);

        if (string.IsNullOrWhiteSpace(content))
        {
            WarnOnce(warnings, warned, $"item {index} has no content — skipped");
            return null;
        }

        var id = FieldOrNull(item, mapping.IdField);
        var url = FieldOrNull(item, mapping.UrlField);
        string uri;
        if (id is { Length: > 0 })
            uri = $"rest:{id}";
        else if (url is { Length: > 0 })
            uri = url;
        else
            uri = $"rest:{Sha256Hex(item.GetRawText())}";

        return new RawDocument(uri, title, content);
    }

    /// <summary>Scalar string for a configured dot-path, or null when the path is
    /// unset or does not resolve to a non-null scalar.</summary>
    private static string? FieldOrNull(JsonElement item, string? field) =>
        field is null
            ? null
            : ScalarOrNull(JsonPathResolver.Resolve(item, field));

    private static string JoinContentFields(
        JsonElement item, string[] fields, List<string> warnings, HashSet<string> warned)
    {
        var parts = new List<string>(fields.Length);
        foreach (var field in fields)
        {
            if (JsonPathResolver.Resolve(item, field) is not { } value)
            {
                WarnOnce(warnings, warned, $"field '{field}' not found");
                continue;
            }
            parts.Add(value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.GetRawText());
        }
        return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>Default content when no <c>contentFields</c>: <c>key: value</c>
    /// per line, nested objects/arrays compacted as JSON (RF-002).</summary>
    private static string DefaultSerialization(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText();

        var lines = new List<string>();
        foreach (var property in item.EnumerateObject()
            .Where(p => p.Value.ValueKind != JsonValueKind.Null))
        {
            lines.Add(property.Value.ValueKind == JsonValueKind.String
                ? $"{property.Name}: {property.Value.GetString()}"
                : $"{property.Name}: {property.Value.GetRawText()}");
        }
        return string.Join("\n", lines);
    }

    private static string? ScalarOrNull(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
    }

    private static void WarnOnce(List<string> warnings, HashSet<string> warned, string message)
    {
        if (warned.Add(message))
            warnings.Add(message);
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
