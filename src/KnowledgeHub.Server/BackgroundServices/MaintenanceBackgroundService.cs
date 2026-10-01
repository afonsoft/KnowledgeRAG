using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Ingestion.Staging;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.BackgroundServices;

/// <summary>
/// SPEC-20260924-hosted-services-and-serilog-logging RF-003: spaced-out
/// maintenance — purges staging directories that no longer belong to a known
/// source (deleted sources normally self-clean, so these are leftovers).
/// Runs on a long interval and never interferes with active ingestion.
/// </summary>
public sealed class MaintenanceBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MaintenanceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(
            Math.Max(1, configuration.GetValue("Maintenance:IntervalHours", 6)));

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PurgeOrphanedStagingAsync(stoppingToken);
                await PurgeA2aTasksAsync(stoppingToken);
                await VacuumVectorStoreAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Maintenance pass failed — retrying next cycle");
            }
        }
    }

    private async Task PurgeOrphanedStagingAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var staging = scope.ServiceProvider.GetRequiredService<IStagingStorageService>();

        var knownIds = (await db.Sources.AsNoTracking()
            .Select(s => s.Id)
            .ToListAsync(ct)).ToHashSet();

        var removed = await staging.CleanupOrphanedStagingAsync(knownIds, ct);
        if (removed > 0)
            logger.LogInformation("Maintenance purged {Count} orphaned staging directorie(s)", removed);
    }

    /// <summary>SPEC-20261001-a2a-task-durability RF-001: purges A2A tasks
    /// older than <c>A2a:TaskRetentionHours</c> (default 72h) — durable
    /// storage grows with every delegated task otherwise.</summary>
    private async Task PurgeA2aTasksAsync(CancellationToken ct)
    {
        var retention = TimeSpan.FromHours(
            Math.Max(1, configuration.GetValue("A2a:TaskRetentionHours", 72)));
        var cutoff = DateTimeOffset.UtcNow - retention;

        await using var scope = scopeFactory.CreateAsyncScope();
        var removed = await scope.ServiceProvider
            .GetRequiredService<A2A.EfA2aTaskStore>()
            .PurgeOlderThanAsync(cutoff, ct);
        if (removed > 0)
            logger.LogInformation(
                "Maintenance purged {Count} A2A task(s) past {Hours}h retention",
                removed, retention.TotalHours);
    }

    /// <summary>SPEC-20260925-pgvector-source-cascade RF-003: weekly VACUUM
    /// ANALYZE on kh_embeddings — pgvector provider only.</summary>
    private DateTimeOffset _lastVacuum = DateTimeOffset.MinValue;

    private async Task VacuumVectorStoreAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("VectorStore:Provider", "sqlite")
                .Equals("postgres", StringComparison.OrdinalIgnoreCase))
            return;
        if (DateTimeOffset.UtcNow - _lastVacuum < TimeSpan.FromDays(7))
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<VectorStore.IVectorStore>()
                is VectorStore.PostgresVectorStore pg)
        {
            await pg.VacuumAnalyzeAsync(ct);
            _lastVacuum = DateTimeOffset.UtcNow;
            logger.LogInformation("Maintenance VACUUM ANALYZE kh_embeddings completed");
        }
    }
}
