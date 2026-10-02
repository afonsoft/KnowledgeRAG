using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>
/// Incremental Obsidian vault indexing (SPEC-03 RF-004): scans <c>**/*.md</c> under the
/// configured path, skips unchanged files by SHA-256, re-chunks + re-embeds changed
/// files, removes documents whose files disappeared. One sync per source at a time.
/// </summary>
public sealed class IngestionService : IIngestionService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IEmbeddingProvider embeddings;
    private readonly IConfiguration configuration;
    private readonly IEnumerable<Connectors.ISourceConnector> connectors;
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache cache;
    private readonly Security.IContentSanitizer sanitizer;
    private readonly Settings.IGraphSettingsService graphSettings;
    private readonly Settings.IEmbeddingSettingsService embeddingSettings;
    private readonly ILogger<IngestionService> logger;
    private readonly Caching.ICacheInvalidationBus? invalidationBus;

    public IngestionService(IngestionServiceDeps deps, IConfiguration configuration,
        ILogger<IngestionService> logger, Caching.ICacheInvalidationBus? invalidationBus = null)
    {
        scopeFactory = deps.ScopeFactory;
        embeddings = deps.Embeddings;
        this.configuration = configuration;
        connectors = deps.Connectors;
        cache = deps.Cache;
        sanitizer = deps.Sanitizer;
        graphSettings = deps.GraphSettings;
        embeddingSettings = deps.EmbeddingSettings;
        this.logger = logger;
        this.invalidationBus = invalidationBus;
    }

    private const string SyncStatusFailed = "failed";
    private const string SyncStatusCompleted = "completed";

    private const long MaxFileBytes = 5 * 1024 * 1024;
    /// <summary>SPEC-20260929 RF-003: the mass-delete gate only protects
    /// populated indexes — sources with fewer indexed docs can legitimately
    /// empty out, so below this count deletions proceed normally.</summary>
    private const int MinMassDeleteDocs = 4;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SourceLocks = new();

    public Task<SyncResultDto> SyncAsync(
        Guid sourceId, SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        // SPEC-20260923-observability-metrics: span + duration/chunk metrics
        // around the whole sync, whichever status it returns with.
        var span = Telemetry.KnowledgeHubActivity.Start("sync");
        span?.SetTag("sync.sourceId", sourceId.ToString("N"));
        var sw = Stopwatch.StartNew();
        return TrackSyncAsync(SyncCoreAsync(sourceId, options, cancellationToken), span, sw);
    }

    private static async Task<SyncResultDto> TrackSyncAsync(
        Task<SyncResultDto> task, Activity? span, Stopwatch sw)
    {
        var status = SyncStatusFailed;
        var chunks = 0L;
        try
        {
            var result = await task;
            status = result.Status;
            chunks = result.ChunksCreated;
            return result;
        }
        catch (OperationCanceledException) { status = "canceled"; throw; }
        catch (Exception ex)
        {
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            throw;
        }
        finally
        {
            span?.SetTag("sync.status", status);
            span?.Dispose();
            var tag = new KeyValuePair<string, object?>("status", status);
            Telemetry.KnowledgeHubMetrics.SyncDuration.Record(sw.Elapsed.TotalMilliseconds, tag);
            if (chunks > 0)
                Telemetry.KnowledgeHubMetrics.SyncChunks.Add(chunks, tag);
        }
    }

    private async Task<SyncResultDto> SyncCoreAsync(
        Guid sourceId, SyncOptions? options, CancellationToken cancellationToken = default)
    {
        var gate = SourceLocks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
            return new SyncResultDto { Status = "skipped", Reason = "sync already running", SourceId = sourceId };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();

            var source = await db.Sources.FindAsync([sourceId], cancellationToken);
            if (source is null)
                return Fail(sourceId, "source not found");

            if (source.SourceType != SourceType.ObsidianVault)
            {
                // SPEC-20260914-webpage-docfile-connectors RF-003: route to the
                // typed connector, then run the shared dedup→chunk→embed pipeline.
                var connector = connectors.FirstOrDefault(c => c.Type == source.SourceType);
                if (connector is null)
                {
                    var skipReason = $"connector {source.SourceType} not implemented";
                    source.LastSyncStatus = "skipped";
                    source.LastError = skipReason;
                    await db.SaveChangesAsync(cancellationToken);
                    return new SyncResultDto { Status = "skipped", Reason = skipReason, SourceId = sourceId, DurationMs = stopwatch.Elapsed.TotalMilliseconds };
                }

                return await SyncViaConnectorAsync(new ConnectorSyncContext(
                    source, connector, db, vectors, scope, stopwatch, options), cancellationToken);
            }

            return await SyncVaultAsync(
                source,
                new PipelineContext(db, vectors, scope, options, stopwatch),
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed for source {SourceId}", sourceId);
            // SPEC-20260926-sync-error-diagnostics RF-001: persist the real
            // cause (innermost exception), not the EF wrapper text.
            var digest = ExceptionDigest.Describe(ex);
            await TryRecordSyncFailureAsync(sourceId, digest);
            return Fail(sourceId, $"sync failed: {digest}", stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Shared pipeline for non-vault connectors: dedup by content hash, chunk,
    /// embed, remove documents no longer returned by the fetch.
    /// </summary>
    private sealed record ConnectorSyncContext(
        KnowledgeSource Source,
        Connectors.ISourceConnector Connector,
        KnowledgeHubDbContext Db,
        IVectorStore Vectors,
        AsyncServiceScope Scope,
        Stopwatch Stopwatch,
        SyncOptions? Options);

    private async Task<SyncResultDto> SyncViaConnectorAsync(
        ConnectorSyncContext ctx,
        CancellationToken cancellationToken)
    {
        var (source, connector, db, vectors, scope, stopwatch, options) =
            (ctx.Source, ctx.Connector, ctx.Db, ctx.Vectors, ctx.Scope, ctx.Stopwatch, ctx.Options);
        // SPEC-20260919-notion-connector RF-007: load existing hashes before the
        // fetch — incremental connectors use the UriReference→ContentHash map to
        // skip re-fetching unchanged remote items (Notion last_edited_time).
        var existing = await db.Documents
            .Where(d => d.KnowledgeSourceId == source.Id)
            .ToDictionaryAsync(d => d.UriReference, cancellationToken);

        var fetch = connector is Connectors.IIncrementalSourceConnector incremental
            ? await incremental.FetchAsync(
                source,
                existing.ToDictionary(kv => kv.Key, kv => kv.Value.ContentHash ?? ""),
                cancellationToken)
            : await connector.FetchAsync(source, cancellationToken);

        var stats = new SyncStats { GraphBudget = graphSettings.GetEffective().MaxChunksPerSync };
        var warnings = new List<string>(fetch.Warnings);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // RF-002: upstream items that failed to download/extract still exist —
        // their indexed documents count as "seen" and are never deleted here.
        if (fetch.FailedUris is { Count: > 0 } failedUris)
        {
            seen.UnionWith(failedUris);
            warnings.Add($"{failedUris.Count} item(s) failed to download/extract — indexed content kept");
        }
        // RF-002: a listing cut by a provider cap cannot prove absence — skip
        // the unseen-deletion pass entirely for this sync.
        if (fetch.Truncated)
        {
            warnings.Add("remote listing was truncated by provider limit — deletion pass skipped");
        }

        // SPEC-20260924-async-ingestion-queue RF-003: chunker versioning —
        // content-identical docs are still re-chunked when the chunker
        // version or chunking-relevant config changed.
        // SPEC-20260926-settings-ux-embeddings RF-004: chunking knobs from
        // the embedding settings store over env.
        var itemCtx = new SyncItemContext
        {
            Source = source,
            Db = db,
            Vectors = vectors,
            Scope = scope,
            Existing = existing,
            Warnings = warnings,
            Chunking = ChunkingPlanFor(source),
            ForceReindex = options?.ForceReindex == true,
            Connector = connector
        };

        foreach (var raw in fetch.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(raw.UriReference);
            await ProcessFetchedItemAsync(itemCtx, raw, stats, cancellationToken);
            options?.Progress?.Report(new SyncProgress(stats.Processed, stats.Skipped, stats.Failed, stats.ChunksCreated));
        }

        stats.Removed = await ReconcileFetchDeletionsAsync(itemCtx, seen, fetch, cancellationToken);

        source.LastSyncAt = DateTimeOffset.UtcNow;
        source.LastSyncStatus = SyncStatusCompleted;
        source.LastError = warnings.Count == 0 ? null
            : $"{warnings.Count} item(s) skipped or flagged: {string.Join("; ", warnings.Take(5))}";
        await db.SaveChangesAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ILexicalSearchService>()
            .ReconcileAsync(cancellationToken);
        await BumpIndexVersionAsync(cancellationToken);

        return new SyncResultDto
        {
            Status = SyncStatusCompleted,
            SourceId = source.Id,
            DocumentsProcessed = stats.Processed,
            DocumentsSkipped = stats.Skipped,
            DocumentsFailed = stats.Failed,
            DocumentsRemoved = stats.Removed,
            ChunksCreated = stats.ChunksCreated,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Reason = warnings.Count == 0 ? null : $"{warnings.Count} item(s) skipped or flagged: {string.Join("; ", warnings.Take(5))}",
            Warnings = warnings.Count == 0 ? null : warnings
        };
    }

    /// <summary>Services/options shared by the vault sync pipeline.</summary>
    private sealed record PipelineContext(
        KnowledgeHubDbContext Db,
        IVectorStore Vectors,
        AsyncServiceScope Scope,
        SyncOptions? Options,
        Stopwatch Stopwatch);

    /// <summary>Chunking knobs resolved once per sync (SPEC-20260924
    /// async-ingestion-queue RF-003 chunker versioning).</summary>
    private sealed record ChunkingPlan(int MaxTokens, int OverlapTokens, string? Strategy, string ConfigHash);

    /// <summary>Per-item inputs shared by the vault and connector sync loops.</summary>
    private sealed record SyncItemContext
    {
        public required KnowledgeSource Source { get; init; }
        public required KnowledgeHubDbContext Db { get; init; }
        public required IVectorStore Vectors { get; init; }
        public required AsyncServiceScope Scope { get; init; }
        public required Dictionary<string, KnowledgeDocument> Existing { get; init; }
        public required List<string> Warnings { get; init; }
        public required ChunkingPlan Chunking { get; init; }
        public required bool ForceReindex { get; init; }
        public Connectors.ISourceConnector? Connector { get; init; }
    }

    /// <summary>Mutable counters accumulated by the per-item sync loops.</summary>
    private sealed class SyncStats
    {
        public int Processed;
        public int Skipped;
        public int Failed;
        public int Removed;
        public int ChunksCreated;
        public int GraphBudget;
    }

    /// <summary>Inputs for <see cref="ReplaceDocumentAsync"/>.</summary>
    private sealed record DocumentReplacePlan
    {
        public required KnowledgeHubDbContext Db { get; init; }
        // Mutable so ReplaceDocumentAsync can publish a newly-created doc for
        // the caller's failure path (DetachPartialAsync) even when it throws.
        public KnowledgeDocument? Doc { get; set; }
        public required KnowledgeSource Source { get; init; }
        public required string Title { get; init; }
        public required string UriReference { get; init; }
        public required string Content { get; init; }
        public required string Hash { get; init; }
        public required string ConfigHash { get; init; }
        public required Chunking.ChunkKind Kind { get; init; }
        public required IReadOnlyList<Chunking.ChunkPiece> Pieces { get; init; }
        public required List<string> Warnings { get; init; }
    }

    /// <summary>Vault sync: enumerate <c>**/*.md</c>, re-index changed files,
    /// remove documents whose files disappeared.</summary>
    private async Task<SyncResultDto> SyncVaultAsync(
        KnowledgeSource source, PipelineContext pipe, CancellationToken cancellationToken)
    {
        var (db, vectors, scope, options, stopwatch) =
            (pipe.Db, pipe.Vectors, pipe.Scope, pipe.Options, pipe.Stopwatch);
        var sourceId = source.Id;

        var root = ResolveVaultRoot(source.ConfigurationJson);
        if (root is null || !Directory.Exists(root))
        {
            // SPEC-20260914-obsidian-webdav RF-002: a missing vault path usually
            // means an unmounted remote — surface that on the source record.
            var reason = $"vault path '{root ?? "(not configured)"}' not found — mount unavailable?";
            source.LastSyncStatus = SyncStatusFailed;
            source.LastError = reason;
            await db.SaveChangesAsync(cancellationToken);
            return Fail(sourceId, reason);
        }

        var files = EnumerateMarkdown(root);
        // SPEC-20260916-performance-memory-cache RF-004: no Include(Chunks) —
        // the old code materialized every chunk's text + embedding BLOB for
        // the whole source. Changed docs purge via ExecuteDeleteAsync below.
        var existing = await db.Documents
            .Where(d => d.KnowledgeSourceId == sourceId)
            .ToDictionaryAsync(d => d.UriReference, cancellationToken);

        var stats = new SyncStats { GraphBudget = graphSettings.GetEffective().MaxChunksPerSync };
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemCtx = new SyncItemContext
        {
            Source = source,
            Db = db,
            Vectors = vectors,
            Scope = scope,
            Existing = existing,
            Warnings = warnings,
            Chunking = ChunkingPlanFor(source),
            ForceReindex = options?.ForceReindex == true
        };

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            seen.Add(relative);
            await ProcessVaultFileAsync(itemCtx, file, relative, stats, cancellationToken);
            options?.Progress?.Report(new SyncProgress(stats.Processed, stats.Skipped, stats.Failed, stats.ChunksCreated));
        }

        stats.Removed = await ReconcileDeletionsAsync(itemCtx, seen, cancellationToken);

        source.LastSyncAt = DateTimeOffset.UtcNow;
        source.LastSyncStatus = SyncStatusCompleted;
        source.LastError = null;
        await db.SaveChangesAsync(cancellationToken);

        // RF-004: keep the FTS index consistent with Chunks after sync.
        await scope.ServiceProvider.GetRequiredService<ILexicalSearchService>()
            .ReconcileAsync(cancellationToken);
        await BumpIndexVersionAsync(cancellationToken);

        return new SyncResultDto
        {
            Status = SyncStatusCompleted,
            SourceId = sourceId,
            DocumentsProcessed = stats.Processed,
            DocumentsSkipped = stats.Skipped,
            DocumentsFailed = stats.Failed,
            DocumentsRemoved = stats.Removed,
            ChunksCreated = stats.ChunksCreated,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Warnings = warnings.Count == 0 ? null : warnings
        };
    }

    /// <summary>SPEC-20260924-async-ingestion-queue RF-003/RF-004 (settings-ux
    /// RF-004): chunker version + chunking-relevant config fingerprint — knobs
    /// come from the embedding settings store over env.</summary>
    private ChunkingPlan ChunkingPlanFor(KnowledgeSource source)
    {
        var (maxTokens, overlapTokens) = embeddingSettings.GetChunking();
        var strategy = Chunking.ChunkerSelector.StrategyFor(source.ConfigurationJson);
        var configHash = Chunking.ChunkerSelector.ConfigHash(
            maxTokens, overlapTokens,
            configuration.GetValue("Ingestion:ContextualEnrichment", "structural"),
            configuration.GetValue("Ingestion:ContextualEnrichment:MinTokens", 40),
            strategy);
        return new ChunkingPlan(maxTokens, overlapTokens, strategy, configHash);
    }

    /// <summary>Unchanged-doc skip: same content hash, no force reindex, and
    /// current chunker version + config hash.</summary>
    private static bool IsCurrent(KnowledgeDocument doc, string hash, bool forceReindex, string configHash) =>
        doc.ContentHash == hash
        && !forceReindex
        && doc.ChunkerVersion == Chunking.ChunkerSelector.CurrentVersion
        && doc.ChunkerConfigHash == configHash;

    /// <summary>Reads, dedups, chunks, replaces, embeds and graph-extracts a
    /// single vault file. SPEC-20260924-async-ingestion-queue RF-002: one bad
    /// document never aborts the job — recorded and the loop continues.</summary>
    private async Task ProcessVaultFileAsync(
        SyncItemContext ctx, string file, string relative,
        SyncStats stats, CancellationToken cancellationToken)
    {
        KnowledgeDocument? doc = null;
        DocumentReplacePlan? plan = null;
        try
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

            if (ctx.Existing.TryGetValue(relative, out doc)
                && IsCurrent(doc, hash, ctx.ForceReindex, ctx.Chunking.ConfigHash))
            {
                stats.Skipped++;
                return;
            }

            var note = MarkdownNoteParser.Parse(content, Path.GetFileName(file));
            // SPEC-20260923-code-aware-chunking: kind from the file extension.
            var (kind, pieces) = await Chunking.ChunkerSelector.ChunkAsync(
                new Chunking.ChunkerSelector.ChunkRequest(relative, note.Body, ctx.Chunking.MaxTokens, ctx.Chunking.OverlapTokens, ctx.Chunking.Strategy),
                embeddings, configuration, logger, cancellationToken, ctx.Scope.ServiceProvider);

            plan = new DocumentReplacePlan
            {
                Db = ctx.Db,
                Doc = doc,
                Source = ctx.Source,
                Title = note.Title,
                UriReference = relative,
                Content = content,
                Hash = hash,
                ConfigHash = ctx.Chunking.ConfigHash,
                Kind = kind,
                Pieces = pieces,
                Warnings = ctx.Warnings
            };
            (doc, var newChunks) = await ReplaceDocumentAsync(plan, cancellationToken);
            stats.ChunksCreated += await EmbedChunksAsync(newChunks, doc, ctx.Source, ctx.Vectors, cancellationToken);
            stats.GraphBudget -= await ExtractGraphAsync(
                ctx.Source, doc, newChunks, ctx.Scope, ctx.Warnings, stats.GraphBudget, cancellationToken);
            stats.Processed++;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Document '{Path}' failed during sync — continuing", relative);
            // SPEC-20260926-job-error-details RF-003: record the real
            // cause (inner exception), not the EF wrapper message.
            ctx.Warnings.Add($"{relative}: {ex.GetBaseException().Message}");
            stats.Failed++;
            // RF-006 (SPEC-20260926-ingestion-connector-integrity): detach
            // this doc's partial tracked state — the next document's
            // SaveChanges must not persist or re-fail on it.
            DetachPartialAsync(plan?.Doc ?? doc, ctx.Db);
        }
    }

    /// <summary>Processes one fetched item: empty-stub content resolution,
    /// fingerprint dedup, chunk → replace → embed → graph.</summary>
    private async Task ProcessFetchedItemAsync(
        SyncItemContext ctx, Connectors.RawDocument raw,
        SyncStats stats, CancellationToken cancellationToken)
    {
        KnowledgeDocument? doc = null;
        DocumentReplacePlan? plan = null;
        try
        {
            ctx.Existing.TryGetValue(raw.UriReference, out doc);

            var text = await ResolveItemTextAsync(ctx, raw, doc, cancellationToken);
            if (text is null)
            {
                stats.Failed++;
                return;
            }

            // RF-007: a connector-supplied fingerprint (upstream change marker)
            // replaces the content hash for dedup — unchanged items arrive with
            // empty TextContent and must not overwrite stored RawContent.
            var hash = string.IsNullOrEmpty(raw.Fingerprint)
                ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
                : raw.Fingerprint;
            if (doc is not null && IsCurrent(doc, hash, ctx.ForceReindex, ctx.Chunking.ConfigHash))
            {
                stats.Skipped++;
                return;
            }

            var (kind, pieces) = await Chunking.ChunkerSelector.ChunkAsync(
                new Chunking.ChunkerSelector.ChunkRequest(raw.UriReference, text, ctx.Chunking.MaxTokens, ctx.Chunking.OverlapTokens, ctx.Chunking.Strategy),
                embeddings, configuration, logger, cancellationToken, ctx.Scope.ServiceProvider);

            plan = new DocumentReplacePlan
            {
                Db = ctx.Db,
                Doc = doc,
                Source = ctx.Source,
                Title = raw.Title,
                UriReference = raw.UriReference,
                Content = text,
                Hash = hash,
                ConfigHash = ctx.Chunking.ConfigHash,
                Kind = kind,
                Pieces = pieces,
                Warnings = ctx.Warnings
            };
            (doc, var newChunks) = await ReplaceDocumentAsync(plan, cancellationToken);
            stats.ChunksCreated += await EmbedChunksAsync(newChunks, doc, ctx.Source, ctx.Vectors, cancellationToken);
            stats.GraphBudget -= await ExtractGraphAsync(
                ctx.Source, doc, newChunks, ctx.Scope, ctx.Warnings, stats.GraphBudget, cancellationToken);
            stats.Processed++;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Document '{Uri}' failed during sync — continuing", raw.UriReference);
            ctx.Warnings.Add($"{raw.UriReference}: {ex.GetBaseException().Message}");
            stats.Failed++;
            // RF-006: detach this doc's partially-mutated tracked state so the
            // next document's SaveChanges cannot persist (or fail on) it.
            DetachPartialAsync(plan?.Doc ?? doc, ctx.Db);
        }
    }

    /// <summary>RF-001: an empty-text stub (unchanged-fingerprint marker) must
    /// never overwrite stored content — when the doc still needs reprocessing
    /// (force/chunker change), falls back to stored RawContent, then to the
    /// connector's on-demand item fetch. Null = keep the doc as-is.</summary>
    private async Task<string?> ResolveItemTextAsync(
        SyncItemContext ctx, Connectors.RawDocument raw, KnowledgeDocument? doc,
        CancellationToken cancellationToken)
    {
        var text = raw.TextContent;
        if (text.Length > 0)
            return text;

        text = doc?.RawContent ?? "";
        if (text.Length == 0 && ctx.Connector is Connectors.IItemFetchConnector itemFetch)
        {
            var fetched = await itemFetch.FetchItemAsync(
                ctx.Source, raw.UriReference, cancellationToken);
            if (fetched is not null)
                text = fetched.TextContent;
        }
        if (text.Length == 0)
        {
            ctx.Warnings.Add($"{raw.UriReference}: no content available for reprocessing — kept as-is");
            return null;
        }
        return text;
    }

    /// <summary>RF-201 (SPEC-20260926-review-backlog-remediation): delete +
    /// replace inside a transaction — a chunker/scan/save failure previously
    /// left the doc with ZERO chunks (the ExecuteDelete is immediate; detaching
    /// could not restore them).</summary>
    private async Task<(KnowledgeDocument Doc, List<DocumentChunk> Chunks)> ReplaceDocumentAsync(
        DocumentReplacePlan plan, CancellationToken cancellationToken)
    {
        var doc = plan.Doc;
        List<DocumentChunk> newChunks;
        await using (var tx = await plan.Db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                if (doc is null)
                {
                    doc = new KnowledgeDocument
                    {
                        KnowledgeSourceId = plan.Source.Id,
                        Title = plan.Title,
                        UriReference = plan.UriReference
                    };
                    plan.Db.Documents.Add(doc);
                    plan.Doc = doc;
                }
                else
                {
                    doc.Title = plan.Title;
                    // Purge old chunks with a direct DELETE — no BLOB/text
                    // materialization, no tracked-collection pitfalls.
                    await plan.Db.Chunks.Where(c => c.KnowledgeDocumentId == doc.Id)
                        .ExecuteDeleteAsync(cancellationToken);
                }

                doc.RawContent = plan.Content;
                doc.ContentHash = plan.Hash;
                doc.ChunkerVersion = Chunking.ChunkerSelector.CurrentVersion;
                doc.ChunkerConfigHash = plan.ConfigHash;
                doc.IndexedAt = DateTimeOffset.UtcNow;

                // AddRange via DbSet — reassigning doc.Chunks after RemoveRange makes EF Core
                // emit an UPDATE for the deleted rows inside the same batch (concurrency error).
                newChunks = plan.Pieces.Select((piece, i) => new DocumentChunk
                {
                    KnowledgeDocumentId = doc.Id,
                    ChunkIndex = i,
                    TextContent = piece.Text,
                    ChunkKind = plan.Kind.ToString().ToLowerInvariant(),
                    SymbolPath = piece.SymbolPath,
                    SectionPath = piece.SectionPath,
                    MetadataJson = piece.MetadataJson,
                    EnrichedText = EnrichPiece(plan.Source.Name, plan.Title, piece)
                }).ToList();
                plan.Db.Chunks.AddRange(newChunks);
                ScanChunks(newChunks, doc.Id, plan.Source.Id, plan.Title, plan.Db, plan.Warnings);

                await plan.Db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        return (doc, newChunks);
    }

    /// <summary>Removes vault documents whose files disappeared.
    /// SPEC-20260929 RF-003: mass-delete safety gate — an empty (or near-
    /// empty) enumeration on a POPULATED index cannot prove absence; an
    /// unreadable dir mounts as zero files and would wipe the index.
    /// Small sources (below MinMassDeleteDocs) are exempt: deleting the
    /// only file of a 1–3 doc vault is a legitimate operation, not a
    /// wipe signature.</summary>
    private async Task<int> ReconcileDeletionsAsync(
        SyncItemContext ctx, HashSet<string> seen, CancellationToken cancellationToken)
    {
        var massDeleteSuspicious =
            ctx.Existing.Count >= MinMassDeleteDocs && seen.Count * 2 < ctx.Existing.Count;
        if (massDeleteSuspicious)
        {
            logger.LogWarning(
                "Sync for source {SourceId}: enumeration returned {Seen} item(s) for {Existing} indexed documents — skipping deletions (possible unreadable folder)",
                ctx.Source.Id, seen.Count, ctx.Existing.Count);
            ctx.Warnings.Add($"deletion skipped: enumeration returned {seen.Count} of {ctx.Existing.Count} indexed documents — verify the path is accessible");
            return 0;
        }

        var removed = 0;
        foreach (var (uri, doc) in ctx.Existing)
        {
            if (seen.Contains(uri))
                continue;
            await ctx.Vectors.DeleteByDocumentAsync(doc.Id, cancellationToken);
            ctx.Db.Documents.Remove(doc);
            removed++;
        }
        return removed;
    }

    /// <summary>Removes indexed documents no longer returned by the fetch.
    /// SPEC-20260929 RF-003: same safety gate as the file path — a fetch that
    /// sees &lt;50% of a populated index can't prove mass absence. Sources
    /// below MinMassDeleteDocs are exempt (small indexes legitimately empty).</summary>
    private async Task<int> ReconcileFetchDeletionsAsync(
        SyncItemContext ctx, HashSet<string> seen, Connectors.FetchResult fetch,
        CancellationToken cancellationToken)
    {
        var fetchSuspiciousDrop =
            ctx.Existing.Count >= MinMassDeleteDocs && seen.Count * 2 < ctx.Existing.Count;
        if (fetchSuspiciousDrop && !fetch.Truncated)
        {
            logger.LogWarning(
                "Fetch sync for source {SourceId}: listing returned {Seen} item(s) for {Existing} indexed documents — skipping deletions",
                ctx.Source.Id, seen.Count, ctx.Existing.Count);
            ctx.Warnings.Add($"deletion skipped: listing returned {seen.Count} of {ctx.Existing.Count} indexed documents");
        }

        var removed = 0;
        foreach (var (uri, doc) in ctx.Existing)
        {
            // RF-002: a truncated listing cannot prove absence — keep everything.
            if (seen.Contains(uri) || fetch.Truncated || fetchSuspiciousDrop)
                continue;
            await ctx.Vectors.DeleteByDocumentAsync(doc.Id, cancellationToken);
            ctx.Db.Documents.Remove(doc);
            removed++;
        }
        return removed;
    }

    /// <summary>SPEC-20260926-ingestion-connector-integrity RF-006: after a
    /// per-document failure, detaches the doc and any chunks added this
    /// iteration so a later <see cref="DbContext.SaveChangesAsync"/> cannot
    /// persist partial state or fail again on it.</summary>
    private static void DetachPartialAsync(KnowledgeDocument? doc, KnowledgeHubDbContext db)
    {
        if (doc is null)
            return;
        foreach (var entry in db.ChangeTracker.Entries()
            .Where(e => ReferenceEquals(e.Entity, doc)
                || (e.Entity is DocumentChunk c && c.KnowledgeDocumentId == doc.Id)
                // RF-202: SecurityEvents queued by the scanner for this doc
                // would otherwise persist pointing at a discarded document.
                || (e.Entity is SecurityEvent se && se.DocumentId == doc.Id))
            .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// SPEC-20260923-prompt-injection-guard RF-003: scans each new chunk,
    /// persists flags on the row and one <c>SecurityEvent</c> per flagged chunk
    /// (ids + flag names only — never content), and appends a per-document
    /// warning for the sync result.
    /// </summary>
    private void ScanChunks(
        List<DocumentChunk> chunks, Guid documentId, Guid sourceId, string title,
        KnowledgeHubDbContext db, List<string> warnings)
    {
        var flaggedCount = 0;
        var flagNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            var flags = sanitizer.Scan(chunk.TextContent);
            if (flags.Count == 0)
                continue;
            chunk.SuspicionFlags = string.Join(',', flags);
            flaggedCount++;
            foreach (var f in flags)
                flagNames.Add(f);
            db.SecurityEvents.Add(new SecurityEvent
            {
                SourceId = sourceId,
                DocumentId = documentId,
                ChunkIndex = chunk.ChunkIndex,
                Flags = chunk.SuspicionFlags
            });
        }

        if (flaggedCount > 0)
        {
            logger.LogWarning(
                "Security scan flagged {Count} chunk(s) in '{Title}' ({DocumentId}): {Flags}",
                flaggedCount, title, documentId, string.Join(',', flagNames));
            warnings.Add($"{title}: {flaggedCount} chunk(s) flagged ({string.Join(',', flagNames)})");
        }
    }

    /// <summary>SPEC-20260924-contextual-chunk-enrichment: structural prefix
    /// (Source | Document | Section) for the embedded/indexed text — display
    /// text stays untouched.</summary>
    private string? EnrichPiece(string sourceName, string documentTitle, Chunking.ChunkPiece piece) =>
        ContextEnricher.Compose(
            sourceName, documentTitle,
            piece.SectionPath ?? piece.SymbolPath,
            piece.Text,
            configuration.GetValue("Ingestion:ContextualEnrichment", "structural"),
            configuration.GetValue("Ingestion:ContextualEnrichment:MinTokens", 40));

    /// <summary>Embed + upsert chunks in one batch — a single provider call for
    /// N texts and a single store batch (SPEC-20260916-performance-memory-cache
    /// RF-004). Batch failure falls back to per-chunk so one bad text doesn't
    /// sink the document.</summary>
    private async Task<int> EmbedChunksAsync(
        List<DocumentChunk> chunks, KnowledgeDocument doc, KnowledgeSource source,
        IVectorStore vectors, CancellationToken cancellationToken)
    {
        // SPEC-20260923-pgvector-metadata-upsert RF-003: provenance metadata
        // rides every upsert (pgvector persists it; other stores ignore it).
        var metadata = new Dictionary<string, string>
        {
            ["sourceType"] = source.SourceType.ToString(),
            ["indexedAt"] = doc.IndexedAt.ToString("o")
        };
        IReadOnlyList<float[]> batchVectors;
        // SPEC-20260925-otel-pipeline-spans RF-001: embed is a provider call —
        // isolate its latency from the rest of the job span.
        using var embedSpan = Telemetry.KnowledgeHubActivity.Start("ingestion.embed");
        embedSpan?.SetTag("embed.count", chunks.Count);
        try
        {
            batchVectors = await embeddings.EmbedDocumentBatchAsync(
                chunks.Select(c => c.EnrichedText ?? c.TextContent).ToList(), cancellationToken);
        }
        catch (EmbeddingProviderException ex)
        {
            Telemetry.KnowledgeHubActivity.Fail(embedSpan, ex);
            logger.LogWarning(ex, "Batch embedding failed for document {DocumentId}, falling back to per-chunk", doc.Id);
            return await EmbedChunksIndividuallyAsync(chunks, doc.Id, source.Id, vectors, metadata, cancellationToken);
        }

        try
        {
            var items = chunks.Zip(batchVectors)
                .Select(pair => new VectorUpsert(pair.First.Id, doc.Id, source.Id, pair.Second, metadata))
                .ToList();
            // SPEC-20260925-vectorstore-metrics RF-001: upsert latency + errors.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await vectors.UpsertBatchAsync(items, embeddings.ModelId, cancellationToken);
            }
            catch
            {
                Telemetry.KnowledgeHubMetrics.VectorErrors.Add(1,
                    new KeyValuePair<string, object?>("store", vectors.GetType().Name),
                    new KeyValuePair<string, object?>("op", "upsert"));
                throw;
            }
            finally
            {
                Telemetry.KnowledgeHubMetrics.VectorUpsertDuration.Record(sw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("store", vectors.GetType().Name));
            }
            return items.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Batch vector upsert failed for document {DocumentId}, falling back to per-chunk", doc.Id);
            return await EmbedChunksIndividuallyAsync(chunks, doc.Id, source.Id, vectors, metadata, cancellationToken);
        }
    }

    private async Task<int> EmbedChunksIndividuallyAsync(
        List<DocumentChunk> chunks, Guid documentId, Guid sourceId,
        IVectorStore vectors, IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var chunk in chunks)
        {
            try
            {
                var vector = await embeddings.EmbedDocumentAsync(chunk.EnrichedText ?? chunk.TextContent, cancellationToken);
                await vectors.UpsertAsync(chunk.Id, documentId, sourceId, vector, embeddings.ModelId,
                    metadata, cancellationToken);
                created++;
            }
            catch (EmbeddingProviderException ex)
            {
                logger.LogWarning(ex, "Embedding failed for chunk {ChunkId}", chunk.Id);
            }
        }
        return created;
    }

    /// <summary>
    /// SPEC-20260923-graphrag RF-001: opt-in entity/relation extraction for
    /// sources with <c>graph:true</c>. Best-effort — a failure is a warning,
    /// never a sync failure. Returns chunks consumed from the sync budget.
    /// </summary>
    private async Task<int> ExtractGraphAsync(
        KnowledgeSource source, KnowledgeDocument doc, IReadOnlyList<DocumentChunk> chunks,
        AsyncServiceScope scope, List<string> warnings, int budgetRemaining,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0 || budgetRemaining <= 0 || !GraphEnabledFor(source))
            return 0;

        // SPEC-20260926-kg-alias-conflict-dedup RF-003: dedicated scope for graph
        // work — a failed graph SaveChanges must never leave the caller's
        // DbContext with poisoned Added entries that break every later save.
        var innerScopeFactory = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
        await using var graphScope = innerScopeFactory.CreateAsyncScope();
        var extractor = graphScope.ServiceProvider.GetService<Graph.EntityExtractor>();
        var store = graphScope.ServiceProvider.GetService<Graph.IKnowledgeGraphStore>();
        if (extractor is null || store is null)
            return 0;

        try
        {
            var batch = chunks.Take(budgetRemaining).ToList();
            var result = await extractor.ExtractAsync(batch, cancellationToken);
            if (result is null || (result.Entities.Count == 0 && result.Relations.Count == 0))
                return batch.Count;

            // SPEC-20260927-temporal-episodic-knowledge-graph RF-001: every
            // extraction run that yields facts opens an episode — new nodes and
            // all edges are attributed to it for episodic retrieval.
            var episodes = graphScope.ServiceProvider.GetService<Graph.GraphEpisodeService>();
            var episode = episodes is null
                ? null
                : await episodes.StartIngestionEpisodeAsync(
                    source.Id, $"{doc.Title} ({doc.UriReference})", cancellationToken);

            // Entity resolution: normalized-name merge per RF-003.
            var nodes = new Dictionary<string, Domain.Entities.KgNode>(StringComparer.Ordinal);
            foreach (var e in result.Entities)
            {
                var norm = Graph.EntityResolver.Normalize(e.Name);
                if (norm.Length == 0 || nodes.ContainsKey(norm))
                    continue;
                nodes[norm] = await store.ResolveNodeAsync(
                    e.Name, e.Type, source.Id, cancellationToken, episode?.Id);
            }

            var edges = new List<Domain.Entities.KgEdge>();
            foreach (var r in result.Relations)
            {
                var from = await ResolveRelationNodeAsync(store, nodes, r.From, source.Id, cancellationToken);
                var to = await ResolveRelationNodeAsync(store, nodes, r.To, source.Id, cancellationToken);
                if (from is null || to is null || from.Id == to.Id)
                    continue;
                var evidence = r.EvidenceIndex >= 1 && r.EvidenceIndex <= batch.Count
                    ? batch[r.EvidenceIndex - 1].Id
                    : batch[0].Id;
                edges.Add(new Domain.Entities.KgEdge
                {
                    FromNodeId = from.Id,
                    ToNodeId = to.Id,
                    Kind = Graph.EntityResolver.NormalizeKind(r.Kind),
                    EvidenceChunkId = evidence,
                    KnowledgeDocumentId = doc.Id,
                    KnowledgeSourceId = source.Id,
                    PromptVersion = Graph.EntityExtractor.PromptVersion,
                    EpisodeId = episode?.Id
                });
            }
            var added = await store.AddEdgesAsync(edges, cancellationToken);
            if (added > 0)
                logger.LogInformation(
                    "graph extraction for {Uri}: {Edges} edge(s), {Nodes} node(s)",
                    doc.UriReference, added, nodes.Count);
            return batch.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "graph extraction failed for {Uri} — continuing sync", doc.UriReference);
            // RF-005: surface the real cause (inner SqliteException), not the EF envelope.
            warnings.Add($"graph extraction failed for '{doc.UriReference}': {ex.GetBaseException().Message}");
            return Math.Min(chunks.Count, budgetRemaining);
        }
    }

    /// <summary>Relations may reference entities absent from the entities list —
    /// resolve them the same way (normalized merge), else skip.</summary>
    private static async Task<Domain.Entities.KgNode?> ResolveRelationNodeAsync(
        Graph.IKnowledgeGraphStore store, Dictionary<string, Domain.Entities.KgNode> nodes,
        string name, Guid sourceId, CancellationToken ct)
    {
        var norm = Graph.EntityResolver.Normalize(name);
        if (norm.Length == 0)
            return null;
        if (nodes.TryGetValue(norm, out var node))
            return node;
        node = await store.ResolveNodeAsync(name, null, sourceId, ct);
        nodes[norm] = node;
        return node;
    }

    /// <summary>RF-005: graph work requires both the global flag and the
    /// per-source <c>"graph": true</c> in ConfigurationJson.</summary>
    private bool GraphEnabledFor(KnowledgeSource source)
    {
        if (!graphSettings.GetEffective().Enabled)
            return false;
        try
        {
            using var json = JsonDocument.Parse(source.ConfigurationJson ?? "{}");
            return json.RootElement.TryGetProperty("graph", out var g)
                && g.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Index (or remove) a single file — used by the vault watcher.</summary>
    public async Task SyncFileAsync(Guid sourceId, string relativePath, CancellationToken cancellationToken = default)
    {
        var gate = SourceLocks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();

            var source = await db.Sources.FindAsync([sourceId], cancellationToken);
            if (source is null)
                return;

            var isDocFile = source.SourceType == SourceType.DocumentFile;
            var root = ResolveVaultRoot(source.ConfigurationJson);
            // RF-002: a missing root means the mount is down (vault dir) — record it
            // and bail without touching documents. DocumentFile may point at a file.
            var rootExists = root is not null &&
                (isDocFile ? Directory.Exists(root) || File.Exists(root) : Directory.Exists(root));
            if (!rootExists)
            {
                if (source.LastSyncStatus != SyncStatusFailed)
                {
                    source.LastSyncStatus = SyncStatusFailed;
                    source.LastError = $"vault path '{root ?? "(not configured)"}' not found — mount unavailable?";
                    await db.SaveChangesAsync(cancellationToken);
                }
                return;
            }

            // Clear a previous failure once the mount is reachable again.
            if (source.LastSyncStatus == SyncStatusFailed)
            {
                source.LastSyncStatus = SyncStatusCompleted;
                source.LastError = null;
                await db.SaveChangesAsync(cancellationToken);
            }

            var full = Path.GetFullPath(Path.Combine(root!, relativePath));
            if (!full.StartsWith(root!, StringComparison.OrdinalIgnoreCase))
                return; // path traversal — ignore

            var doc = await db.Documents
                .FirstOrDefaultAsync(d => d.KnowledgeSourceId == sourceId && d.UriReference == relativePath, cancellationToken);

            if (!File.Exists(full)
                || isDocFile && !Connectors.DocumentFileConnector.SupportedExtensions.Contains(Path.GetExtension(full)))
            {
                if (doc is not null)
                {
                    await vectors.DeleteByDocumentAsync(doc.Id, cancellationToken);
                    db.Documents.Remove(doc);
                    await db.SaveChangesAsync(cancellationToken);
                    await scope.ServiceProvider.GetRequiredService<ILexicalSearchService>()
                        .ReconcileAsync(cancellationToken);
                    await BumpIndexVersionAsync(cancellationToken);
                }
                return;
            }

            // DocumentFile hashes/extracts the *text*; vault hashes the raw file.
            string content;
            string title;
            string body;
            if (isDocFile)
            {
                content = await Connectors.DocumentFileConnector.ExtractTextAsync(
                    full, Path.GetExtension(full), cancellationToken) ?? "";
                if (content.Length == 0)
                    return;
                title = Path.GetFileNameWithoutExtension(full);
                body = content;
            }
            else
            {
                content = await File.ReadAllTextAsync(full, cancellationToken);
                var note = MarkdownNoteParser.Parse(content, Path.GetFileName(full));
                title = note.Title;
                body = note.Body;
            }

            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
            if (doc?.ContentHash == hash)
                return;

            var (chunkMaxTokens, chunkOverlapTokens) = embeddingSettings.GetChunking();
            var (kind, pieces) = await Chunking.ChunkerSelector.ChunkAsync(
                new Chunking.ChunkerSelector.ChunkRequest(relativePath, body, chunkMaxTokens,
                    chunkOverlapTokens, Chunking.ChunkerSelector.StrategyFor(source.ConfigurationJson)),
                embeddings, configuration, logger, cancellationToken, scope.ServiceProvider);

            if (doc is null)
            {
                doc = new KnowledgeDocument { KnowledgeSourceId = sourceId, Title = title, UriReference = relativePath };
                db.Documents.Add(doc);
            }
            else
            {
                doc.Title = title;
                await db.Chunks.Where(c => c.KnowledgeDocumentId == doc.Id)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            doc.RawContent = content;
            doc.ContentHash = hash;
            doc.IndexedAt = DateTimeOffset.UtcNow;

            // AddRange via DbSet — see vault path above.
            var watcherWarnings = new List<string>();
            var newChunks = pieces.Select((piece, i) => new DocumentChunk
            {
                KnowledgeDocumentId = doc.Id,
                ChunkIndex = i,
                TextContent = piece.Text,
                ChunkKind = kind.ToString().ToLowerInvariant(),
                SymbolPath = piece.SymbolPath,
                SectionPath = piece.SectionPath,
                MetadataJson = piece.MetadataJson,
                EnrichedText = EnrichPiece(source.Name, title, piece)
            }).ToList();
            db.Chunks.AddRange(newChunks);
            ScanChunks(newChunks, doc.Id, sourceId, title, db, watcherWarnings);

            await db.SaveChangesAsync(cancellationToken);
            await EmbedChunksAsync(newChunks, doc, source, vectors, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<ILexicalSearchService>()
                .ReconcileAsync(cancellationToken);
            await BumpIndexVersionAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Bump the index-version token so cached search results keyed on
    /// it miss after this sync (SPEC-20260916-performance-memory-cache §5).
    /// Best-effort — a down cache must never fail a sync.</summary>
    private async Task BumpIndexVersionAsync(CancellationToken cancellationToken)
    {
        await Caching.SafeCache.SetStringAsync(cache, Caching.CacheKeys.IndexVersion,
            Guid.NewGuid().ToString("N"), null, logger, cancellationToken);
        // SPEC-20260925-distributed-invalidation-pubsub RF-002: other replicas
        // holding the token in L1 must drop it now, not at L1 TTL expiry.
        if (invalidationBus is not null)
            await invalidationBus.PublishAsync("index-version", cancellationToken);
    }

    /// <summary>Best-effort: record a sync failure on the source in a fresh scope.</summary>
    private async Task TryRecordSyncFailureAsync(Guid sourceId, string message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var source = await db.Sources.FindAsync([sourceId]);
            if (source is null)
                return;
            source.LastSyncStatus = SyncStatusFailed;
            source.LastError = message.Length > 1000 ? message[..1000] : message;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record sync failure on source {SourceId}", sourceId);
        }
    }

    internal static string? ResolveVaultRoot(string? configurationJson)
    {
        if (string.IsNullOrEmpty(configurationJson))
            return null;
        try
        {
            using var json = JsonDocument.Parse(configurationJson);
            var path = json.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateMarkdown(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*.md", options))
        {
            var relative = Path.GetRelativePath(root, file);
            if (IsExcluded(relative))
                continue;
            var info = new FileInfo(file);
            if (info.Length == 0 || info.Length > MaxFileBytes)
                continue;
            yield return file;
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return segments.Any(s => s.StartsWith('.') || s.Equals(".obsidian", StringComparison.OrdinalIgnoreCase));
    }

    private static SyncResultDto Fail(Guid sourceId, string reason, double ms = 0) =>
        new() { Status = SyncStatusFailed, Reason = reason, SourceId = sourceId, DurationMs = ms };
}
