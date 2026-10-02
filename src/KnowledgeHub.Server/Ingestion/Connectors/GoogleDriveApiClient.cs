using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// Google Drive client for shared links (SPEC-20260924-gdrive-shared-link-connector).
/// With an API key it uses Drive API v3 (metadata/list/download/export); without
/// one it falls back to the public embedded-folder listing and the public
/// <c>uc?export=download</c> endpoint.
/// </summary>
public sealed class GoogleDriveApiClient(HttpClient http)
{
    private const string ApiBase = "https://www.googleapis.com/drive/v3";

    /// <summary>Drive item as returned by the v3 API or scraped listing.</summary>
    public sealed record DriveFileMeta(
        string Id,
        string Name,
        string MimeType,
        long? Size,
        string? Md5Checksum,
        DateTimeOffset? ModifiedTime);

    private const string FileFields =
        "id,name,mimeType,size,md5Checksum,modifiedTime";

    // SPEC RF-002: accepted shared-link shapes.
    private static readonly Regex FolderPattern = new(
        @"^https?://drive\.google\.com/(?:drive/)?(?:u/\d+/)?folders/([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex FilePattern = new(
        @"^https?://drive\.google\.com/file/d/([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex IdPattern = new(
        @"^https?://drive\.google\.com/.*[?&]id=([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>Parses a shared link into (resourceId, isFolder). False on
    /// unrecognised/invalid links — callers turn that into a 400.</summary>
    public static bool TryParseSharedUrl(string? url, out string resourceId, out bool isFolder)
    {
        resourceId = "";
        isFolder = false;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var m = FolderPattern.Match(url.Trim());
        if (m.Success)
        {
            resourceId = m.Groups[1].Value;
            isFolder = true;
            return true;
        }
        m = FilePattern.Match(url.Trim());
        if (!m.Success)
            m = IdPattern.Match(url.Trim());
        if (m.Success)
        {
            resourceId = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    /// <summary>files.get metadata — only via the v3 API (needs a key).</summary>
    public async Task<DriveFileMeta?> GetFileAsync(
        string id, string? apiKey, CancellationToken ct)
    {
        var url = $"{ApiBase}/files/{Uri.EscapeDataString(id)}?fields={FileFields}{KeyParam(apiKey)}";
        var meta = await http.GetFromJsonAsync<DriveFileDto>(url, Json, ct);
        return meta is null ? null : ToMeta(meta);
    }

    /// <summary>SPEC-20260926-ingestion-connector-integrity RF-005: metadata probe
    /// for public single-file links without an API key — a 1-byte ranged GET on
    /// <c>uc?export=download</c> exposes the real filename (Content-Disposition)
    /// and size (Content-Range/Content-Length) without downloading the body.
    /// Returns null when the file is not publicly readable or has no name.</summary>
    public async Task<DriveFileMeta?> TryGetPublicFileMetaAsync(string id, CancellationToken ct)
    {
        try
        {
            var url = $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(id)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"');
            if (string.IsNullOrWhiteSpace(name))
                return null;
            long? size = response.Content.Headers.ContentRange?.Length
                ?? response.Content.Headers.ContentLength;
            return new DriveFileMeta(
                id, name, response.Content.Headers.ContentType?.MediaType ?? "", size, null, null);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Recursively lists every file under <paramref name="folderId"/>
    /// via the v3 API — yields (meta, parentRelativePath) so callers can build
    /// relative keys. Requires an API key.</summary>
    public async IAsyncEnumerable<(DriveFileMeta Meta, string RelativePath)> ListFolderAsync(
        string folderId, string? apiKey, string prefix = "",
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? pageToken = null;
        do
        {
            var q = Uri.EscapeDataString($"'{folderId}' in parents and trashed = false");
            var url = $"{ApiBase}/files?q={q}&pageSize=1000&fields=nextPageToken,files({FileFields}){KeyParam(apiKey)}"
                + (pageToken is null ? "" : $"&pageToken={pageToken}");
            var page = await http.GetFromJsonAsync<DriveListDto>(url, Json, ct);
            foreach (var meta in (page?.Files ?? []).Select(ToMeta))
            {
                if (meta.MimeType == "application/vnd.google-apps.folder")
                {
                    await foreach (var child in ListFolderAsync(meta.Id, apiKey,
                        $"{prefix}{meta.Name}/", ct))
                        yield return child;
                }
                else
                {
                    yield return (meta, prefix);
                }
            }
            pageToken = page?.NextPageToken;
        } while (pageToken is not null);
    }

    /// <summary>Public (no key) folder listing via the embedded folder view —
    /// names + ids only; no metadata (single level, subfolders are links).
    /// Returns null when the view is unavailable.</summary>
    public async IAsyncEnumerable<DriveFileMeta> ListPublicFolderAsync(
        string folderId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"https://drive.google.com/embeddedfolderview?id={Uri.EscapeDataString(folderId)}#list";
        var html = await http.GetStringAsync(url, ct);
        // Entries look like: <a href="https://drive.google.com/file/d/{id}/view?..." ...>{name}</a>
        foreach (var groups in EmbeddedEntryPattern.Matches(html)
                     .Select(m => m.Groups)
                     .Where(g => !g[1].Value.Contains("/drive/folders/", StringComparison.Ordinal)))
        {
            // public view shows subfolders as links — recursion needs metadata anyway
            yield return new DriveFileMeta(
                groups[1].Value,
                System.Net.WebUtility.HtmlDecode(groups[2].Value.Trim()),
                "", null, null, null);
        }
    }

    private static readonly Regex EmbeddedEntryPattern = new(
        @"href=""https?://drive\.google\.com/(?:file/d/|drive/folders/)([a-zA-Z0-9_-]+)[^""]*""[^>]*>\s*(?:<div[^>]*>)?([^<]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>Binary download — v3 <c>?alt=media</c> with key, public
    /// <c>uc?export=download</c> without.</summary>
    public async Task<Stream> DownloadAsync(string id, string? apiKey, CancellationToken ct)
    {
        var url = apiKey is not null
            ? $"{ApiBase}/files/{Uri.EscapeDataString(id)}?alt=media{KeyParam(apiKey)}"
            : $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(id)}";
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Public endpoint returns an HTML interstitial for non-public/large files.
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (apiKey is null && contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException(
                "Public download is not available for this file — configure a Google API key on the source");
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Native Workspace export — v3 <c>/export?mimeType=</c>.</summary>
    public async Task<Stream> ExportAsync(string id, string mimeType, string? apiKey, CancellationToken ct)
    {
        var url = $"{ApiBase}/files/{Uri.EscapeDataString(id)}/export?mimeType={Uri.EscapeDataString(mimeType)}{KeyParam(apiKey)}";
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private static string KeyParam(string? apiKey) =>
        apiKey is null ? "" : $"&key={Uri.EscapeDataString(apiKey)}";

    private static DriveFileMeta ToMeta(DriveFileDto f) => new(
        f.Id ?? "", f.Name ?? "", f.MimeType ?? "",
        long.TryParse(f.Size, out var sz) ? sz : null,
        f.Md5Checksum,
        DateTimeOffset.TryParse(f.ModifiedTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class DriveFileDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? MimeType { get; set; }
        public string? Size { get; set; }
        public string? Md5Checksum { get; set; }
        public string? ModifiedTime { get; set; }
    }

    private sealed class DriveListDto
    {
        public List<DriveFileDto> Files { get; set; } = [];
        public string? NextPageToken { get; set; }
    }
}
