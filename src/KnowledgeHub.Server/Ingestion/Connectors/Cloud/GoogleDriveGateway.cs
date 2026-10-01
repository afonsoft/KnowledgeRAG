using System.Runtime.CompilerServices;

namespace KnowledgeHub.Server.Ingestion.Connectors.Cloud;

/// <summary>
/// <see cref="IRemoteObjectGateway"/> over Google Drive shared links
/// (SPEC-20260924-gdrive-shared-link-connector RF-003/RF-004/RF-005):
/// folder links enumerate recursively via the v3 API when an API key is
/// configured, or fall back to the public embedded-folder view (flat, names
/// only). Native Workspace docs are exported transparently as text/csv.
/// </summary>
internal sealed class GoogleDriveGateway(
    GoogleDriveApiClient client,
    string rootId,
    bool rootIsFolder,
    string? apiKey,
    int maxFiles) : IRemoteObjectGateway
{
    // native mime → (export mime, staging extension)
    private static readonly Dictionary<string, (string ExportMime, string Ext)> NativeMap = new()
    {
        ["application/vnd.google-apps.document"] = ("text/plain", ".txt"),
        ["application/vnd.google-apps.spreadsheet"] = ("text/csv", ".csv"),
        ["application/vnd.google-apps.presentation"] = ("text/plain", ".txt")
    };

    private readonly Dictionary<string, GoogleDriveApiClient.DriveFileMeta> _byKey = new();

    /// <summary>True when the listing hit the <c>maxFiles</c> cap with more items
    /// upstream — downstream reconciliation must not treat unlisted docs as
    /// deleted (SPEC-20260926-ingestion-connector-integrity RF-002).</summary>
    public bool Truncated { get; private set; }

    public async IAsyncEnumerable<RemoteObject> ListAsync(
        string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = 0;
        IEnumerable<RemoteObject> Emit(GoogleDriveApiClient.DriveFileMeta meta, string dir,
            bool probeOnly = false)
        {
            // Truncated only when a (maxFiles+1)-th object actually exists.
            foreach (var o in Yield(meta, dir, probeOnly))
            {
                if (seen >= maxFiles) { Truncated = true; yield break; }
                seen++;
                yield return o;
            }
        }

        if (!rootIsFolder)
        {
            var meta = apiKey is not null
                ? await client.GetFileAsync(rootId, apiKey, ct)
                : null;
            // Public single-file links: probe Content-Disposition for the real
            // name — an id-as-name has no extension and never indexes (RF-005).
            GoogleDriveApiClient.DriveFileMeta? probed = null;
            var m = meta;
            if (m is null)
            {
                probed = await client.TryGetPublicFileMetaAsync(rootId, ct);
                m = probed ?? new GoogleDriveApiClient.DriveFileMeta(rootId, rootId, "", null, null, null);
            }
            foreach (var o in Emit(m, "", probeOnly: probed is not null)) yield return o;
            yield break;
        }

        if (apiKey is not null)
        {
            await foreach (var (meta, dir) in client.ListFolderAsync(rootId, apiKey, ct: ct))
            {
                foreach (var o in Emit(meta, dir)) yield return o;
                if (Truncated) yield break;
            }
            yield break;
        }

        await foreach (var meta in client.ListPublicFolderAsync(rootId, ct))
        {
            foreach (var o in Emit(meta, "")) yield return o;
            if (Truncated) yield break;
        }
    }

    private IEnumerable<RemoteObject> Yield(
        GoogleDriveApiClient.DriveFileMeta meta, string dir, bool probeOnly = false)
    {
        var key = $"{dir}{meta.Name}";
        if (NativeMap.TryGetValue(meta.MimeType, out var native))
        {
            if (!key.EndsWith(native.Ext, StringComparison.OrdinalIgnoreCase))
                key += native.Ext;
        }
        _byKey[key] = meta;
        // RF-005: fingerprint combines modifiedTime + md5Checksum (size
        // fallback — natives have no md5).
        // RF-205: a public-probe meta carries SIZE only — using it as etag
        // makes a same-size edit look "unchanged". No reliable marker → empty
        // fingerprint → always re-downloaded, never skipped as unchanged.
        var etag = probeOnly ? "" : meta.Md5Checksum ?? meta.Size?.ToString() ?? "";
        // Natives/listing-scraped items have no size — report 1 so the base
        // loop does not skip them as empty; the real byte limit is enforced
        // at download time by maxFileSizeMB.
        yield return new RemoteObject(key, etag, meta.ModifiedTime ?? DateTimeOffset.MinValue,
            meta.Size is > 0 ? meta.Size.Value : 1);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        if (!_byKey.TryGetValue(key, out var meta))
            throw new InvalidOperationException($"Drive key '{key}' was not listed — call ListAsync first");
        return NativeMap.TryGetValue(meta.MimeType, out var native)
            ? client.ExportAsync(meta.Id, native.ExportMime, apiKey, ct)
            : client.DownloadAsync(meta.Id, apiKey, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
