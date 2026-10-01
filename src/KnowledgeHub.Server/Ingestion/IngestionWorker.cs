using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>
/// SPEC-20260924-async-ingestion-queue RF-002: sequential worker draining the
/// ingestion channel. Each job runs the shared <see cref="IIngestionService"/>
/// pipeline with per-document failure isolation and periodic progress flush to
/// the job row. Startup marks orphaned queued/running rows as failed — the
/// per-document pipeline is idempotent, so a fresh sync is always safe.
/// </summary>
public sealed class IngestionWorker(
    IIngestionQueue queue,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IIngestionProgressFeed progressFeed,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private const string StatusQueued = "queued";
    private const string StatusRunning = "running";
    private const string StatusFailed = "failed";
    /// <summary>Synchronous <see cref="IProgress{T}"/> — just stores the latest
    /// snapshot; a timer flushes it to the DB on its own scope.</summary>
    private sealed class LatestProgress(Action<SyncProgress> onReport) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => onReport(value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphanedJobsAsync(stoppingToken);

        await foreach (var jobId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunJobAsync(jobId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Ingestion job {JobId} crashed — continuing with next job", jobId);
                await FailStrandedJobAsync(jobId, ex);
            }
            finally
            {
                queue.Complete(jobId);
            }
        }
    }

    /// <summary>Jobs left queued/running by a previous process are marked
    /// failed — honest recovery, no resume (re-sync is idempotent).</summary>
    private async Task FailOrphanedJobsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var orphans = await db.IngestionJobs
                .Where(j => j.Status == "queued" || j.Status == "running")
                .ToListAsync(ct);
            foreach (var job in orphans)
            {
                job.Status = "failed";
                job.Error = "interrupted by restart";
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
            if (orphans.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                // SPEC-20260928-observability-followups RF-001: persist first,
                // then publish the terminal event per job — subscribers of the
                // progress feed must see the sweep close the row.
                foreach (var job in orphans)
                    progressFeed.Publish(new IngestionProgressEvent(
                        job.Id, job.SourceId, "failed",
                        job.DocsProcessed, job.DocsSkipped, job.DocsFailed,
                        job.ChunksCreated, job.FinishedAt!.Value));
                logger.LogWarning("Marked {Count} orphaned ingestion job(s) as failed", orphans.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Orphaned-job sweep failed");
        }
    }

    private async Task RunJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();

        var job = await db.IngestionJobs.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.Status != "queued")
        {
            // "cancelled" is expected (cancel endpoint fires before dequeue);
            // anything else drops the job with no trace — log it.
            if (job?.Status != "cancelled")
                logger.LogWarning(
                    "Ingestion job {JobId} dequeued with status {Status} — skipping",
                    jobId, job?.Status ?? "missing");
            return;
        }

        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(stoppingToken);
        // SPEC-20260924-hosted-services-and-serilog-logging RF-004: structured
        // job context on every log emitted while the job runs.
        using var logScope = Serilog.Context.LogContext.PushProperty("JobId", jobId);
        using var logScopeSrc = Serilog.Context.LogContext.PushProperty("SourceId", job.SourceId);
        using var logScope2 = Serilog.Context.LogContext.PushProperty("JobKind", job.Kind);
        logger.LogInformation("Ingestion job {JobId} started (kind {Kind}, source {SourceId})",
            jobId, job.Kind, job.SourceId);

        var jobCt = queue.TokenFor(jobId, stoppingToken);
        SyncProgress latest = new(0, 0, 0, 0);
        var progress = new LatestProgress(p =>
        {
            latest = p;
            // SPEC-20260925-job-progress-feed: push throttled ticks to subscribers.
            progressFeed.Publish(new IngestionProgressEvent(
                jobId, job.SourceId, "running",
                p.Processed, p.Skipped, p.Failed, p.ChunksCreated, DateTimeOffset.UtcNow));
        });
        var flushEverySeconds = Math.Max(2,
            configuration.GetValue("Ingestion:ProgressFlushSeconds", 5));
        var flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(flushEverySeconds));
        var flusher = FlushProgressAsync(jobId, () => latest, flushTimer, stoppingToken);

        var options = new SyncOptions
        {
            ForceReindex = job.Kind == "reindex",
            Progress = progress
        };

        try
        {
            // SPEC-20260925-otel-pipeline-spans RF-001: job-level span —
            // sync sub-stages (chunk/embed/graph) hang off it in traces.
            using var jobSpan = Telemetry.KnowledgeHubActivity.Start("ingestion.job");
            jobSpan?.SetTag("job.kind", job.Kind);
            jobSpan?.SetTag("job.source_id", job.SourceId.ToString());
            var result = await ingestion.SyncAsync(job.SourceId, options, jobCt);
            job.Status = result.Status == "failed" ? "failed" : "done";
            // SPEC-20260926-job-error-details RF-001/RF-004: persist per-doc
            // failures and enrich the generic failure reason with a real digest.
            job.WarningsJson = SerializeWarnings(result.Warnings);
            job.Error = result.Status == "failed"
                ? EnrichError(result.Reason, result.Warnings, result.DocumentsFailed)
                : null;
            job.DocsProcessed = result.DocumentsProcessed;
            job.DocsSkipped = result.DocumentsSkipped;
            job.DocsFailed = result.DocumentsFailed;
            job.ChunksCreated = result.ChunksCreated;
        }
        catch (OperationCanceledException) when (jobCt.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            job.Status = "cancelled";
        }
        // SPEC-20260926-sync-error-diagnostics RF-002: a host shutdown cancels
        // jobCt too — record it distinctly instead of "The operation was canceled."
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.Status = "failed";
            job.Error = "interrupted by shutdown/restart";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ingestion job {JobId} failed", jobId);
            job.Status = "failed";
            job.Error = ExceptionDigest.Describe(ex);
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            flushTimer.Dispose();
            try { await flusher; } catch { /* already logged inside */ }
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not persist job {JobId} outcome", jobId); }
            // SPEC-20260925-job-progress-feed: terminal state always published.
            progressFeed.Publish(new IngestionProgressEvent(
                jobId, job.SourceId, job.Status,
                job.DocsProcessed, job.DocsSkipped, job.DocsFailed, job.ChunksCreated,
                DateTimeOffset.UtcNow));
            // SPEC-20260924-hosted-services-and-serilog-logging RF-004: one
            // structured completion line per job with all counters.
            logger.LogInformation(
                "Ingestion job {JobId} finished: status={Status} docs={DocsProcessed} skipped={DocsSkipped} failed={DocsFailed} chunks={ChunksCreated}",
                jobId, job.Status, job.DocsProcessed, job.DocsSkipped, job.DocsFailed, job.ChunksCreated);
        }
    }

    /// <summary>SPEC-20260926-ingestion-jobs-test-deflake: a crash between
    /// dequeue and the persisted "running" transition strands the row as
    /// "queued" — the channel write is consumed, dedup pins it as Existing
    /// forever, and subscribers never see a terminal event. Repair: if the row
    /// is still queued/running, persist failed + publish the terminal event.</summary>
    private async Task FailStrandedJobAsync(Guid jobId, Exception crash)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var job = await db.IngestionJobs.FirstOrDefaultAsync(
                j => j.Id == jobId, CancellationToken.None);
            if (job is null || job.Status is not ("queued" or "running"))
                return;
            job.Status = "failed";
            job.Error = $"worker error before terminal state — {ExceptionDigest.Describe(crash)}";
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            progressFeed.Publish(new IngestionProgressEvent(
                jobId, job.SourceId, "failed",
                job.DocsProcessed, job.DocsSkipped, job.DocsFailed, job.ChunksCreated,
                DateTimeOffset.UtcNow));
        }
        catch (Exception rex)
        {
            logger.LogWarning(rex, "Could not repair stranded ingestion job {JobId}", jobId);
        }
    }

    /// <summary>Periodic counter flush on an isolated scope — the job's own
    /// DbContext stays free for the sync loop.</summary>
    private async Task FlushProgressAsync(
        Guid jobId, Func<SyncProgress> latest, PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var p = latest();
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
                await db.IngestionJobs
                    .Where(j => j.Id == jobId && j.Status == "running")
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(j => j.DocsProcessed, p.Processed)
                        .SetProperty(j => j.DocsSkipped, p.Skipped)
                        .SetProperty(j => j.DocsFailed, p.Failed)
                        .SetProperty(j => j.ChunksCreated, p.ChunksCreated), ct);
            }
        }
        catch (OperationCanceledException) { /* host shutting down */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Progress flush for job {JobId} stopped", jobId);
        }
    }

    /// <summary>SPEC-20260926-job-error-details RF-001: cap persisted warnings —
    /// first <see cref="MaxWarnings"/> entries, else the row balloons on mass failures.</summary>
    private const int MaxWarnings = 100;

    private static string? SerializeWarnings(IReadOnlyList<string>? warnings)
    {
        if (warnings is null || warnings.Count == 0)
            return null;
        var capped = warnings.Count > MaxWarnings
            ? warnings.Take(MaxWarnings).Append($"… e mais {warnings.Count - MaxWarnings} item(ns)").ToList()
            : warnings;
        return System.Text.Json.JsonSerializer.Serialize(capped);
    }

    /// <summary>RF-004: "sync failed — see server logs" tells the operator nothing;
    /// attach the real first error + counts.</summary>
    private static string EnrichError(string? reason, IReadOnlyList<string>? warnings, int failed)
    {
        var first = warnings?.FirstOrDefault(w => !w.StartsWith("graph extraction failed", StringComparison.Ordinal));
        var sample = first ?? warnings?.FirstOrDefault();
        var digest = sample is null
            ? null
            : $"{failed} doc(s) falharam — {sample}";
        if (string.IsNullOrEmpty(reason))
            return digest ?? "sync failed";
        return digest is null ? reason : $"{reason}: {digest}";
    }
}
