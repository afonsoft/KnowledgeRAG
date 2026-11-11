using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002 (mode=async): drains the retain
/// channel. Each op runs <see cref="MemoryRetainService"/> on its own DI
/// scope; cancellation via <see cref="IMemoryRetainQueue.TryCancel"/> aborts
/// before commit (no partial units). Orphaned queued/running ops fail at
/// startup — re-retain is idempotent.
/// </summary>
public sealed class MemoryRetainWorker(
    IMemoryRetainQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<MemoryRetainWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphanedAsync(stoppingToken);
        await foreach (var opId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(opId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "memory operation {Id} crashed — continuing", opId);
            }
            finally
            {
                queue.Complete(opId);
            }
        }
    }

    private async Task FailOrphanedAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var orphans = await db.MemoryOperations
                .Where(o => o.Status == "queued" || o.Status == "running")
                .ToListAsync(ct);
            foreach (var o in orphans)
            {
                o.Status = "failed";
                o.Error = "process restarted before the operation ran";
                o.FinishedAt = DateTime.UtcNow;
            }
            if (orphans.Count > 0)
                await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to mark orphaned memory operations");
        }
    }

    private async Task RunAsync(Guid opId, CancellationToken stopping)
    {
        var ct = queue.TokenFor(opId, stopping);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var op = await db.MemoryOperations.FirstOrDefaultAsync(o => o.Id == opId, ct);
        if (op is null || op.Status is "cancelled" or "done" or "failed")
            return;

        op.Status = "running";
        op.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var req = JsonSerializer.Deserialize<MemoryRetainRequest>(op.PayloadJson)
                ?? throw new InvalidOperationException("corrupt retain payload");
            var bank = await db.MemoryBanks.FirstOrDefaultAsync(b => b.Id == op.BankId, ct)
                ?? throw new InvalidOperationException("memory bank was deleted");
            var retain = scope.ServiceProvider.GetRequiredService<MemoryRetainService>();
            var result = await retain.ExecuteAsync(bank, req, ct);
            op.Status = "done";
            op.UnitsCreated = result.UnitsCreated;
            op.ResultJson = JsonSerializer.Serialize(result);
        }
        catch (OperationCanceledException)
        {
            op.Status = "cancelled";
        }
        catch (Exception ex)
        {
            op.Status = "failed";
            op.Error = ex.Message;
        }
        op.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
