using System.Security.Cryptography;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// UnstructuredDocument connector (SPEC-20260927-unstructured-document-parser-connector):
/// sends binary documents (.pdf/.docx/.pptx/.xlsx/.jpg/.tiff…) to an
/// Unstructured.io-compatible parsing endpoint and emits the structured
/// elements rendered as clean Markdown (tables → GFM, titles → headings).
/// The API key lives in the encrypted store (<c>unstructured:{sourceId}</c>),
/// never in <c>ConfigurationJson</c>. Incremental sync fingerprints each file
/// as <c>unstructured:{file}:{sha256}:{strategy}</c> — unchanged files skip
/// the HTTP call entirely (RF-003).
/// </summary>
public sealed class UnstructuredDocumentConnector(
    UnstructuredApiClient api,
    IIntegrationSecretStore secrets,
    ILogger<UnstructuredDocumentConnector> logger)
    : ISourceConnector, IIncrementalSourceConnector
{
    /// <summary>Secret-store slot for the API key (optional — local endpoints).</summary>
    public static string SecretKey(Guid sourceId) => $"unstructured:{sourceId}";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".pptx", ".xlsx", ".jpg", ".jpeg", ".png", ".tiff"
    };

    private static readonly HashSet<string> ValidStrategies = new(StringComparer.OrdinalIgnoreCase)
        { "auto", "hi_res", "ocr_only", "fast" };

    public SourceType Type => SourceType.UnstructuredDocument;

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var folderPath = config.String("folderPath") ?? config.String("path");
        var files = config.StringArray("files");
        var apiUrl = config.String("apiUrl") ?? UnstructuredApiClient.DefaultEndpoint;
        var strategy = config.String("strategy") ?? "auto";
        if (!ValidStrategies.Contains(strategy))
            throw new InvalidOperationException(
                $"UnstructuredDocument source '{source.Name}': invalid strategy '{strategy}' (auto|hi_res|ocr_only|fast)");
        var coordinates = config.Bool("coordinates");
        var tableExtraction = config.Bool("tableExtraction", fallback: true);
        var maxBytes = config.Int("maxFileSizeMb", 25, 1, 100) * 1024L * 1024;

        var apiKey = await secrets.GetAsync(SecretKey(source.Id), cancellationToken);

        var paths = EnumerateFiles(folderPath ?? "", files);
        if (paths.Count == 0)
            return new FetchResult([], ["no supported files found in the configured path/files list"]);

        var documents = new List<RawDocument>();
        var warnings = new List<string>();
        var failedUris = new List<string>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            var uri = $"unstructured://{fileName}";

            try
            {
                var info = new FileInfo(path);
                if (info.Length > maxBytes)
                {
                    warnings.Add($"{fileName}: skipped — {info.Length / 1024 / 1024}MB exceeds maxFileSizeMb");
                    logger.LogDebug("unstructured skip {File}: over maxFileSizeMb", fileName);
                    continue;
                }

                // RF-003: fingerprint = file hash + strategy — unchanged files
                // emit an empty-content stub so reconciliation keeps the doc.
                string fingerprint;
                {
                    await using var fs = File.OpenRead(path);
                    var sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken))[..16];
                    fingerprint = $"unstructured:{fileName}:{sha}:{strategy}";
                }
                if (existingFingerprints.TryGetValue(uri, out var prev) && prev == fingerprint)
                {
                    documents.Add(new RawDocument(uri, fileName, "", fingerprint));
                    continue;
                }

                var elements = await api.ParseAsync(
                    apiUrl, apiKey, path, strategy, coordinates, tableExtraction, cancellationToken);
                var markdown = UnstructuredElementRenderer.Render(elements);
                documents.Add(new RawDocument(uri, fileName, markdown, fingerprint));
            }
            catch (UnstructuredApiException ex)
            {
                // Auth failure poisons every subsequent call — fail the sync
                // with an explicit message instead of warning 50 times (AC-4).
                if (ex.Status is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden)
                    throw new InvalidOperationException(
                        $"UnstructuredDocument source '{source.Name}': API authentication failed (HTTP {(int)ex.Status}) — check the stored apiKey", ex);
                failedUris.Add(uri);
                warnings.Add($"{fileName}: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedUris.Add(uri);
                warnings.Add($"{fileName}: {ex.Message}");
            }
        }

        // Endpoint down for every file → fail the sync with a retry hint;
        // partial failures stay warnings so intact docs still reconcile.
        if (documents.Count == 0 && failedUris.Count == paths.Count && paths.Count > 0)
            throw new InvalidOperationException(
                $"UnstructuredDocument source '{source.Name}': all {paths.Count} files failed — endpoint may be down, retry the sync later");

        return new FetchResult(documents, warnings, failedUris);
    }

    /// <summary>folderPath (directory, non-recursive) ∪ files (explicit list).</summary>
    private static List<string> EnumerateFiles(string? folderPath, string[] files)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            paths.AddRange(Directory.EnumerateFiles(folderPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f))));
        paths.AddRange(files
            .Where(f => File.Exists(f) && SupportedExtensions.Contains(Path.GetExtension(f))));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
    }
}
