using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Staging;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Ingestion.Connectors.Cloud;

/// <summary>
/// Shared fetch loop for remote object-storage connectors
/// (SPEC-20260924-cloud-storage-connectors RF-003..RF-005): list → glob/ext/size
/// filters → incremental fingerprint dedup → download to staging → extract →
/// emit <see cref="RawDocument"/> with an <c>{etag}|{lastModified}</c>
/// fingerprint so unchanged objects skip re-download and re-chunk.
/// </summary>
internal abstract class CloudConnectorBase(IStagingStorageService staging, ILogger logger)
    : IIncrementalSourceConnector, IItemFetchConnector
{
    public abstract SourceType Type { get; }

    /// <summary>Builds the authenticated gateway for this source — secrets come
    /// from the store inside this method (never from raw config).</summary>
    protected abstract Task<IRemoteObjectGateway> CreateGatewayAsync(
        KnowledgeSource source, ConnectorConfig config, CancellationToken ct);

    /// <summary>Canonical document URI (e.g. <c>s3://bucket/key</c>).</summary>
    protected abstract string UriFor(ConnectorConfig config, string key);

    /// <summary>Configurable for tests.</summary>
    internal Func<KnowledgeSource, ConnectorConfig, CancellationToken, Task<IRemoteObjectGateway>>? GatewayOverride { get; set; }

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var glob = config.String("glob") ?? "**/*";
        var matcher = DocumentFileConnector.GlobMatcher.Compile(glob);
        var maxBytes = config.Int("maxFileSizeMB", 20, 1, 512) * 1024L * 1024;
        var prefix = config.String("prefix") ?? config.String("directoryPath");

        await using var gateway = GatewayOverride is not null
            ? await GatewayOverride(source, config, cancellationToken)
            : await CreateGatewayAsync(source, config, cancellationToken);

        var documents = new List<RawDocument>();
        var warnings = new List<string>();
        // RF-002: items that exist upstream but fail to download/extract — the
        // sync pipeline keeps (does NOT delete) their indexed documents.
        var failedUris = new List<string>();
        var stagingDir = staging.GetStagingDirectory(source.Id);
        var loopCtx = new ObjectLoopContext
        {
            Config = config,
            Matcher = matcher,
            MaxBytes = maxBytes,
            ExistingFingerprints = existingFingerprints,
            Gateway = gateway,
            StagingDir = stagingDir,
            Documents = documents,
            Warnings = warnings,
            FailedUris = failedUris
        };

        await foreach (var obj in gateway.ListAsync(prefix, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessObjectAsync(loopCtx, obj, cancellationToken);
        }

        return new FetchResult(documents, warnings,
            failedUris.Count > 0 ? failedUris : null, gateway.Truncated);
    }

    /// <summary>Per-object inputs for the fetch loop.</summary>
    private sealed record ObjectLoopContext
    {
        public required ConnectorConfig Config { get; init; }
        public required Func<string, bool> Matcher { get; init; }
        public required long MaxBytes { get; init; }
        public required IReadOnlyDictionary<string, string> ExistingFingerprints { get; init; }
        public required IRemoteObjectGateway Gateway { get; init; }
        public required string StagingDir { get; init; }
        public required List<RawDocument> Documents { get; init; }
        public required List<string> Warnings { get; init; }
        public required List<string> FailedUris { get; init; }
    }

    /// <summary>Applies glob/ext/size filters and fingerprint dedup to one
    /// object, then downloads + extracts it. Per-object failures warn and
    /// land in FailedUris — never abort the fetch.</summary>
    private async Task ProcessObjectAsync(
        ObjectLoopContext ctx, RemoteObject obj, CancellationToken ct)
    {
        var uri = UriFor(ctx.Config, obj.Key);
        try
        {
            if (!ctx.Matcher(obj.Key))
                return;
            var ext = Path.GetExtension(obj.Key);
            if (!DocumentFileConnector.SupportedExtensions.Contains(ext))
            {
                ctx.Warnings.Add($"{obj.Key}: unsupported extension '{ext}'");
                return;
            }
            if (obj.Size == 0 || obj.Size > ctx.MaxBytes)
            {
                ctx.Warnings.Add($"{obj.Key}: size {obj.Size} outside 1..{ctx.MaxBytes} bytes");
                return;
            }

            // Incremental: same upstream marker → keep stored doc, no download.
            // RF-005: an EMPTY fingerprint proves nothing — always download.
            if (obj.Fingerprint is { Length: > 0 } fingerprint
                && ctx.ExistingFingerprints.TryGetValue(uri, out var stored)
                && string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                ctx.Documents.Add(new RawDocument(uri, Path.GetFileNameWithoutExtension(obj.Key), "", fingerprint));
                return;
            }

            await DownloadExtractAsync(ctx, obj, uri, ext, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.FailedUris.Add(uri);
            logger.LogWarning(ex, "Failed to process {Key} — skipped", obj.Key);
            ctx.Warnings.Add($"{obj.Key}: {ex.Message}");
        }
    }

    /// <summary>Downloads one object to the staging dir and extracts its text.
    /// Failures keep the indexed document alive via FailedUris (RF-002).</summary>
    private async Task DownloadExtractAsync(
        ObjectLoopContext ctx, RemoteObject obj, string uri, string ext, CancellationToken ct)
    {
        var localPath = Path.Combine(ctx.StagingDir, Sanitize(obj.Key));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await using (var remote = await ctx.Gateway.OpenReadAsync(obj.Key, ct))
            await using (var local = File.Create(localPath))
            {
                await remote.CopyToAsync(local, ct);
            }

            var text = await DocumentFileConnector.ExtractTextAsync(localPath, ext, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                ctx.Warnings.Add($"{obj.Key}: no extractable text");
                ctx.FailedUris.Add(uri); // exists upstream, extraction failed — keep indexed doc
                return;
            }
            ctx.Documents.Add(new RawDocument(uri, Path.GetFileNameWithoutExtension(obj.Key), text, obj.Fingerprint));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeletePartial(localPath);
            ctx.FailedUris.Add(uri); // transient failure ≠ remote delete (RF-002)
            logger.LogWarning(ex, "Failed to download/extract {Key} — skipped", obj.Key);
            ctx.Warnings.Add($"{obj.Key}: {ex.Message}");
        }
    }

    /// <summary>RF-001: on-demand single-item fetch — locates the object by URI,
    /// downloads through the same staging/extract path, returns null on miss.</summary>
    public async Task<RawDocument?> FetchItemAsync(
        KnowledgeSource source, string uriReference, CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var maxBytes = config.Int("maxFileSizeMB", 20, 1, 512) * 1024L * 1024;

        await using var gateway = GatewayOverride is not null
            ? await GatewayOverride(source, config, cancellationToken)
            : await CreateGatewayAsync(source, config, cancellationToken);

        var found = await gateway.ListAsync(null, cancellationToken)
            .Where(obj => UriFor(config, obj.Key) == uriReference)
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null || found.Size <= 0 || found.Size > maxBytes)
            return null;
        var ext = Path.GetExtension(found.Key);
        if (!DocumentFileConnector.SupportedExtensions.Contains(ext))
            return null;

        var stagingDir = staging.GetStagingDirectory(source.Id);
        var localPath = Path.Combine(stagingDir, Sanitize(found.Key));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await using (var remote = await gateway.OpenReadAsync(found.Key, cancellationToken))
            await using (var local = File.Create(localPath))
            {
                await remote.CopyToAsync(local, cancellationToken);
            }
            var text = await DocumentFileConnector.ExtractTextAsync(localPath, ext, cancellationToken);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new RawDocument(uriReference, Path.GetFileNameWithoutExtension(found.Key), text, found.Fingerprint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeletePartial(localPath);
            logger.LogWarning(ex, "On-demand fetch failed for {Key}", found.Key);
            return null;
        }
    }

    /// <summary>Maps a remote key to a safe local relative path (no traversal).</summary>
    private static string Sanitize(string key)
    {
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(parts.Select(p => p is "." or ".." ? "_" : p).ToArray());
    }

    private void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove partial download {Path}", path);
        }
    }
}
