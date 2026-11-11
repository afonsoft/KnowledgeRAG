using System.Threading.Channels;
using KnowledgeHub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-memory-observations-consolidation RF-001 (a): post-retain
/// trigger — a retain with new units signals the bank; the worker coalesces
/// signals (deduplicated channel-free via HashSet drain) and runs
/// consolidation. Unbounded channel, TryWrite — signal is best-effort.
/// </summary>
public interface IMemoryConsolidationTrigger
{
    ChannelReader<Guid> Reader { get; }
    void Signal(Guid bankId);
}

public sealed class MemoryConsolidationTrigger : IMemoryConsolidationTrigger
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public void Signal(Guid bankId) => _channel.Writer.TryWrite(bankId);
}

/// <summary>
/// SPEC-20261111-memory-observations-consolidation RF-001 (b): scheduled
/// consolidation — every <c>Memory:Consolidation:IntervalMinutes</c> (default
/// 15) scans for banks with unconsolidated units and runs each on its own DI
/// scope. Busy banks (manual run in flight) are skipped via the shared lock.
/// Failed facts re-enter after <c>RetryBackoffMinutes</c>.
/// </summary>
public sealed class MemoryConsolidationWorker(
    IMemoryConsolidationTrigger trigger,
    MemoryConfigResolver config,
    IServiceScopeFactory scopeFactory,
    ILogger<MemoryConsolidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = config.ConsolidationInterval();
        using var timer = new PeriodicTimer(
            interval < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
                var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                var signaled = trigger.Reader.WaitToReadAsync(stoppingToken).AsTask();
                await Task.WhenAny(tick, signaled);
                while (trigger.Reader.TryRead(out var bankId))
                    await RunBankAsync(bankId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "memory consolidation worker tick failed");
            }
        }
    }

    /// <summary>Runs every bank that has pending facts (backoff-aware).</summary>
    internal async Task ScanAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var cfg = scope.ServiceProvider.GetRequiredService<MemoryConfigResolver>();
        var retryAfter = DateTime.UtcNow - cfg.ConsolidationRetryBackoff();
        var bankIds = await db.MemoryUnits
            .Where(u => !u.IsDeleted && u.ConsolidatedAt == null
                && (u.FactType == "world" || u.FactType == "experience")
                && (u.ConsolidationFailedAt == null || u.ConsolidationFailedAt < retryAfter))
            .Select(u => u.BankId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var bankId in bankIds)
            await RunBankAsync(bankId, ct);
    }

    private async Task RunBankAsync(Guid bankId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var bank = await db.MemoryBanks.FirstOrDefaultAsync(b => b.Id == bankId, ct);
        if (bank is null)
            return;
        var svc = scope.ServiceProvider.GetRequiredService<MemoryConsolidationService>();
        try
        {
            await svc.RunAsync(bank, ct);
        }
        catch (MemoryConflictException)
        {
            // Another run holds the bank — skipped, next tick covers it.
        }
        catch (MemoryServiceUnavailableException ex)
        {
            logger.LogDebug(ex, "consolidation skipped for bank {Bank} — no chat provider", bank.BankKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "consolidation run failed for bank {Bank}", bank.BankKey);
        }
    }
}
