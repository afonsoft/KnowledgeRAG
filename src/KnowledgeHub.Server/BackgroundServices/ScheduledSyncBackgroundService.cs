using System.Collections.Concurrent;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.BackgroundServices;

/// <summary>
/// SPEC-20260924-hosted-services-and-serilog-logging RF-002 + SPEC-20260925-
/// autosync-through-queue: periodic auto-sync for sources with
/// <c>AutoSyncEnabled</c>. Jobs are enqueued via <see cref="IIngestionQueue"/>
/// so auto-syncs are auditable like manual ones. Failures are logged per source
/// and never block the loop.
/// </summary>
public sealed class ScheduledSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IIngestionQueue queue,
    ILogger<ScheduledSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFullSync = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueSyncsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled sync loop failed — retrying");
            }
            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    private async Task RunDueSyncsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var autoSources = await db.Sources
            .Where(s => s.IsActive && s.AutoSyncEnabled
                && (s.SourceType == SourceType.ObsidianVault
                    || s.SourceType == SourceType.WebPage
                    || s.SourceType == SourceType.DocumentFile
                    || s.SourceType == SourceType.Notion
                    || s.SourceType == SourceType.AwsS3
                    || s.SourceType == SourceType.AzureFiles
                    || s.SourceType == SourceType.OciStorage
                    || s.SourceType == SourceType.GoogleDrive
                    || s.SourceType == SourceType.RestApi
                    || s.SourceType == SourceType.SqlDatabase
                    || s.SourceType == SourceType.RssFeed
                    || s.SourceType == SourceType.YouTube
                    || s.SourceType == SourceType.UnstructuredDocument))
            .Select(s => new { s.Id, s.SyncIntervalMinutes })
            .ToListAsync(ct);

        foreach (var source in autoSources)
        {
            var interval = TimeSpan.FromMinutes(source.SyncIntervalMinutes ?? 30);
            var last = _lastFullSync.GetValueOrDefault(source.Id, DateTimeOffset.MinValue);
            if (DateTimeOffset.UtcNow - last < interval)
                continue;
            _lastFullSync[source.Id] = DateTimeOffset.UtcNow;
            try
            {
                await queue.EnqueueAsync(source.Id, "autosync", ct);
                logger.LogInformation("Auto-sync enqueued for source {SourceId} (interval {IntervalMinutes}min)",
                    source.Id, source.SyncIntervalMinutes ?? 30);
            }
            catch (QueueFullException)
            {
                logger.LogWarning("Ingestion queue full — auto-sync for source {SourceId} deferred", source.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-sync failed for source {SourceId}", source.Id);
            }
        }
    }
}
