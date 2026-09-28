using System.Net;
using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// SPEC-20260927-unstructured-document-parser-connector RF-001: thin client for
/// the Unstructured.io <c>/general/v0/general</c> endpoint (or a self-hosted
/// compatible one — Upstage included). Posts the file as multipart/form-data
/// with the extraction strategy; the API key rides the
/// <c>unstructured-api-key</c> header and comes only from the secret store.
/// </summary>
public sealed class UnstructuredApiClient(
    IHttpClientFactory httpClientFactory,
    ILogger<UnstructuredApiClient> logger)
{
    public const string DefaultEndpoint = "https://api.unstructured.io/general/v0/general";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tiff" };

    /// <summary>Image inputs can't run <c>fast</c>/<c>auto</c> — the API requires
    /// OCR for them (RF-001 rule).</summary>
    public static string EffectiveStrategy(string strategy, string extension) =>
        ImageExtensions.Contains(extension) && strategy is "auto" or "fast"
            ? "ocr_only"
            : strategy;

    /// <summary>POSTs the file and returns the element array. Throws
    /// <see cref="UnstructuredApiException"/> carrying the HTTP status so the
    /// connector can distinguish auth/availability failures.</summary>
    public async Task<JsonElement> ParseAsync(
        string apiUrl, string? apiKey, string filePath,
        string strategy, bool coordinates, bool tableExtraction,
        CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("unstructured");

        await using var stream = File.OpenRead(filePath);
        var fileName = Path.GetFileName(filePath);
        var effective = EffectiveStrategy(strategy, Path.GetExtension(filePath));

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "files", fileName);
        form.Add(new StringContent(effective), "strategy");
        if (coordinates)
            form.Add(new StringContent("true"), "coordinates");
        if (tableExtraction)
            form.Add(new StringContent("true"), "pdf_infer_table_structure");
        form.Add(new StringContent("true"), "include_page_breaks");

        using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = form };
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("unstructured-api-key", apiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("unstructured API {Status} for {File}: {Body}",
                (int)response.StatusCode, fileName, Truncate(body));
            throw new UnstructuredApiException(response.StatusCode,
                $"unstructured API returned {(int)response.StatusCode} for '{fileName}'"
                + (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? " — invalid or missing apiKey"
                    : ""));
        }

        await using var json = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>Non-success response from the Unstructured-compatible endpoint.</summary>
public sealed class UnstructuredApiException(HttpStatusCode status, string message)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
