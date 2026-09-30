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
    : IIncrementalSourceConnector
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
            var (uri, fileName, relativePath) = ResolveFileUri(folderPath, path);
            var job = new FileJob(source, path, uri, fileName, relativePath,
                apiUrl, strategy, coordinates, tableExtraction, maxBytes, apiKey, existingFingerprints);
            var outcome = await ProcessFileAsync(job, cancellationToken);
            if (outcome.Document is not null)
                documents.Add(outcome.Document);
            if (outcome.Warning is not null)
                warnings.Add(outcome.Warning);
            if (outcome.Failed)
                failedUris.Add(uri);
        }

        // Endpoint down for every file → fail the sync with a retry hint;
        // partial failures stay warnings so intact docs still reconcile.
        if (documents.Count == 0 && failedUris.Count == paths.Count && paths.Count > 0)
            throw new InvalidOperationException(
                $"UnstructuredDocument source '{source.Name}': all {paths.Count} files failed — endpoint may be down, retry the sync later");

        return new FetchResult(documents, warnings, failedUris);
    }

    /// <summary>Per-file processing inputs for <see cref="ProcessFileAsync"/>.</summary>
    private sealed record FileJob(
        KnowledgeSource Source, string Path, string Uri, string FileName, string RelativePath,
        string ApiUrl, string Strategy, bool Coordinates, bool TableExtraction, long MaxBytes,
        string? ApiKey, IReadOnlyDictionary<string, string> ExistingFingerprints);

    /// <summary>Outcome of one file: a document, or a warning + failure flag.</summary>
    private sealed record FileOutcome(RawDocument? Document, string? Warning, bool Failed);

    /// <summary>SPEC-20260929 RF-002: URI is the path relative to the configured
    /// root — homonymous files in different folders must not share a doc.
    /// Without folderPath, explicit `files` can point anywhere — hash the full
    /// path so same-name files keep distinct URIs.</summary>
    private static (string Uri, string FileName, string RelativePath) ResolveFileUri(
        string? folderPath, string path)
    {
        var fileName = Path.GetFileName(path);
        var relativePath = folderPath is { Length: > 0 } root
            ? Path.GetRelativePath(root, path)
            : $"{Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12]}/{fileName}";
        return ($"unstructured://{relativePath.Replace('\\', '/')}", fileName, relativePath);
    }

    /// <summary>Size gate → fingerprint gate → parse+render (RF-003/RF-004);
    /// API auth failures poison the sync (AC-4), other failures warn.</summary>
    private async Task<FileOutcome> ProcessFileAsync(FileJob job, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(job.Path);
            if (info.Length > job.MaxBytes)
            {
                logger.LogDebug("unstructured skip {File}: over maxFileSizeMb", job.FileName);
                return new FileOutcome(null,
                    $"{job.FileName}: skipped — {info.Length / 1024 / 1024}MB exceeds maxFileSizeMb", false);
            }

            // RF-003: fingerprint = file hash + strategy — unchanged files
            // emit an empty-content stub so reconciliation keeps the doc.
            // SPEC-20260929 RF-004: extraction-shaping options join the
            // fingerprint — changing strategy/tables/coordinates must
            // re-extract, not reuse stale output.
            var fingerprint =
                $"unstructured:{job.RelativePath}:{await FileFingerprintAsync(job.Path, ct)}"
                + $":{job.Strategy}:{job.TableExtraction}:{job.Coordinates}";
            if (job.ExistingFingerprints.TryGetValue(job.Uri, out var prev) && prev == fingerprint)
                return new FileOutcome(
                    new RawDocument(job.Uri, job.FileName, "", fingerprint), null, false);

            var elements = await api.ParseAsync(
                job.ApiUrl, job.ApiKey, job.Path, job.Strategy,
                job.Coordinates, job.TableExtraction, ct);
            var markdown = UnstructuredElementRenderer.Render(elements);
            return new FileOutcome(
                new RawDocument(job.Uri, job.FileName, markdown, fingerprint), null, false);
        }
        catch (UnstructuredApiException ex)
        {
            // Auth failure poisons every subsequent call — fail the sync
            // with an explicit message instead of warning 50 times (AC-4).
            if (ex.Status is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    $"UnstructuredDocument source '{job.Source.Name}': API authentication failed (HTTP {(int)ex.Status}) — check the stored apiKey", ex);
            return new FileOutcome(null, $"{job.FileName}: {ex.Message}", true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileOutcome(null, $"{job.FileName}: {ex.Message}", true);
        }
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

    /// <summary>SHA-256 fingerprint (16 hex chars) of the local file bytes —
    /// combined with the extraction options into the doc fingerprint.</summary>
    private static async Task<string> FileFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken))[..16];
    }
}
