using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// SPEC-20260926-ingestion-jobs-test-deflake: a crash between dequeue and the
/// persisted "running" transition must not strand the row as "queued" — the
/// channel write is consumed, dedup would pin it as Existing forever, and
/// waiters never see a terminal event. The worker repairs it to "failed".
/// </summary>
public sealed class IngestionWorkerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private readonly IngestionQueue _queue;
    private readonly IngestionProgressFeed _feed = new();

    public IngestionWorkerTests()
    {
        _conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite(_conn));
        // IIngestionService intentionally NOT registered — resolving it inside
        // RunJobAsync is a deterministic crash before the "running" transition.
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>()
            .Database.EnsureCreated();

        _queue = new IngestionQueue(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
    }

    private async Task<Guid> SeedSourceAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private IngestionWorker CreateWorker() => new(
        _queue,
        _provider.GetRequiredService<IServiceScopeFactory>(),
        new ConfigurationBuilder().Build(),
        _feed,
        NullLogger<IngestionWorker>.Instance);

    /// <summary>Subscribes BEFORE the job is enqueued so the terminal event can
    /// never race ahead of the subscription.</summary>
    private Task<IngestionProgressEvent> WaitTerminalForAsync(Guid sourceId)
    {
        var terminal = new TaskCompletionSource<IngestionProgressEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _feed.Published += e =>
        {
            if (e.SourceId == sourceId && e.Status is "done" or "failed" or "cancelled")
                terminal.TrySetResult(e);
        };
        return terminal.Task;
    }

    [Fact]
    public async Task CrashBeforeRunning_FailsJob_AndPublishesTerminalEvent()
    {
        var sourceId = await SeedSourceAsync();
        using var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300); // let the orphan sweep finish before enqueueing

        var terminal = WaitTerminalForAsync(sourceId);
        var (job, _) = await _queue.EnqueueAsync(sourceId, "sync", CancellationToken.None);
        var evt = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("failed", evt.Status);
        Assert.Equal(job.Id, evt.JobId);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var row = await db.IngestionJobs.FindAsync(job.Id);
        Assert.Equal("failed", row!.Status);
        Assert.NotNull(row.Error);
        Assert.NotNull(row.FinishedAt);
    }

    // SPEC-20260928-observability-followups RF-001/AC-1: a job left "queued" by
    // a previous process must be marked failed AND publish its terminal event —
    // the progress feed must not leave the job stuck open in the UI.
    [Fact]
    public async Task OrphanSweep_PublishesTerminalEvent()
    {
        var sourceId = await SeedSourceAsync();
        var jobId = Guid.NewGuid();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            db.IngestionJobs.Add(new IngestionJob
            {
                Id = jobId,
                SourceId = sourceId,
                Kind = "sync",
                Status = "queued"
            });
            await db.SaveChangesAsync();
        }

        var terminal = WaitTerminalForAsync(sourceId);
        using var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);

        var evt = await terminal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", evt.Status);
        Assert.Equal(jobId, evt.JobId);

        await using var verify = _provider.CreateAsyncScope();
        var row = await verify.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>()
            .IngestionJobs.FindAsync(jobId);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("interrupted by restart", row.Error);
        Assert.NotNull(row.FinishedAt);
    }

    [Fact]
    public async Task CancelledBeforeDequeue_IsSkippedAndLoopContinues()
    {
        var sourceId = await SeedSourceAsync();

        // Cancelled before the worker starts — the orphan sweep only touches
        // queued/running, so this row survives to exercise the skip path.
        var (job, _) = await _queue.EnqueueAsync(sourceId, "sync", CancellationToken.None);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var row = await db.IngestionJobs.FindAsync(job.Id);
            row!.Status = "cancelled";
            row.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var worker = CreateWorker();
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300); // let the orphan sweep finish before enqueueing

        // FIFO: the sentinel is only dequeued after the cancelled job — its
        // terminal event proves the skip didn't wedge the loop.
        var terminal = WaitTerminalForAsync(sourceId);
        var (sentinel, _) = await _queue.EnqueueAsync(sourceId, "sync", CancellationToken.None);
        var evt = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(sentinel.Id, evt.JobId);
        Assert.Equal("failed", evt.Status);
        await using var verify = _provider.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var final = await verifyDb.IngestionJobs.FindAsync(job.Id);
        Assert.Equal("cancelled", final!.Status);
    }
}
