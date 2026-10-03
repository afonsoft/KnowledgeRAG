using KnowledgeHub.Server.Data;

namespace KnowledgeHub.Server.Flows;

/// <summary>
/// Dispatches <c>schedule</c> flow triggers: polls <see cref="FlowService.DueSchedulesAsync"/>
/// every <c>Flows:SchedulerPollSeconds</c> (default 30s) and fires each due trigger
/// through the normal <see cref="FlowService.RunAsync"/> path. Best-effort —
/// a failed run is logged and the trigger's LastFiredAt still advances so one
/// bad run doesn't wedge the schedule.
/// </summary>
public sealed class FlowSchedulerWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FlowSchedulerWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _pollSeconds =
        configuration.GetValue("Flows:SchedulerPollSeconds", 30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations/seed settle before the first sweep.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "flow scheduler sweep failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var flows = scope.ServiceProvider.GetRequiredService<FlowService>();
        var due = await flows.DueSchedulesAsync(DateTimeOffset.UtcNow, ct);
        foreach (var trigger in due)
        {
            try
            {
                var result = await flows.FireScheduleAsync(trigger, scope.ServiceProvider, ct);
                logger.LogInformation(
                    "schedule trigger {TriggerId} → flow run {RunId} ({Status})",
                    trigger.Id, result?.RunId, result?.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "schedule trigger {TriggerId} run failed", trigger.Id);
            }

            // Advance the schedule even on failure — a permanently-broken
            // trigger should not retry-storm every poll.
            trigger.LastFiredAt = DateTimeOffset.UtcNow;
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            db.FlowTriggers.Update(trigger);
            await db.SaveChangesAsync(ct);
        }
    }
}


