using System.Threading.Channels;
using KnowledgeHub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory.MentalModels;

/// <summary>Enqueue a model refresh (hook, worker scan, or manual trigger).</summary>
public interface IMentalModelRefreshTrigger
{
    void Signal(Guid modelId);
}

/// <summary>
/// SPEC-20261111-mental-models-pages RF-002: drains refresh signals and scans
/// models whose interval triggers are due. Each refresh is serialized per
/// model by <see cref="MentalModelRefreshLocks"/>.
/// </summary>
public sealed class MentalModelsWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MentalModelsWorker> logger) : BackgroundService, IMentalModelRefreshTrigger
{
    private readonly Channel<Guid> _signals =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Signal(Guid modelId) => _signals.Writer.TryWrite(modelId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scan = Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        var signal = _signals.Reader.WaitToReadAsync(stoppingToken).AsTask();
        while (!stoppingToken.IsCancellationRequested)
        {
            var done = await Task.WhenAny(scan, signal);
            try
            {
                if (done == scan)
                {
                    await ScanDueAsync(stoppingToken);
                    scan = Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                else
                {
                    while (_signals.Reader.TryRead(out var id))
                        await RefreshAsync(id, stoppingToken);
                    signal = _signals.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Mental model refresh cycle failed");
            }
        }
    }

    private async Task RefreshAsync(Guid modelId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MentalModelRefreshService>();
        await svc.RefreshAsync(modelId, ignoreMinInterval: false, ct);
    }

    /// <summary>Interval-driven refresh: models whose delta/full interval elapsed.</summary>
    private async Task ScanDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<MentalModelRefreshService>();
        var models = await db.MemoryMentalModels.AsNoTracking().ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var m in models)
        {
            var t = MentalModelService.Trigger(m);
            var fullDue = t.FullRefreshIntervalMinutes is { } fi
                && (m.LastRefreshedAt is null
                    || now - m.LastRefreshedAt >= TimeSpan.FromMinutes(fi));
            var deltaDue = t.RefreshIntervalMinutes is { } ri
                && (m.LastRefreshedAt is null
                    || now - m.LastRefreshedAt >= TimeSpan.FromMinutes(ri));
            // full interval also implies re-synthesis; the model's trigger mode
            // decides the pass — full-due models refresh even when delta-mode.
            if (fullDue || deltaDue)
                await svc.RefreshAsync(m.Id, ignoreMinInterval: fullDue, ct);
        }
    }
}

/// <summary>
/// SPEC-20261111-mental-models-pages RF-002: post-consolidation hook —
/// signals models whose scope may overlap the consolidated units. Staleness
/// is re-evaluated at refresh time (models with nothing new just watermark).
/// </summary>
public sealed class MentalModelConsolidationHook(
    IServiceProvider sp,
    IMentalModelRefreshTrigger trigger) : IMemoryConsolidationHook
{
    public async Task OnConsolidatedAsync(
        Domain.Entities.MemoryBank bank, MemoryConsolidateResult result, CancellationToken ct)
    {
        if (result.Created + result.Updated + result.Deleted == 0)
            return;
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var models = await db.MemoryMentalModels.AsNoTracking()
            .Where(m => m.BankId == bank.Id)
            .Select(m => new { m.Id, m.TriggerJson, m.Subtype })
            .ToListAsync(ct);
        foreach (var m in models)
            if (MentalModelTrigger.Parse(m.TriggerJson, m.Subtype).RefreshAfterConsolidation)
                trigger.Signal(m.Id);
    }
}
