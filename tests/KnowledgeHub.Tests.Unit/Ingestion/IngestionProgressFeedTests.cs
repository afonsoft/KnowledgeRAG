using KnowledgeHub.Server.Ingestion;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Ingestion;

/// <summary>
/// SPEC-20260929-observability-and-tests-residual RF-001/AC-1: the progress
/// feed keeps the last event per job so a subscriber that connects after a
/// terminal publish (e.g. the orphan sweep right after restart) still sees it.
/// </summary>
public sealed class IngestionProgressFeedTests
{
    private static IngestionProgressEvent Evt(
        Guid jobId, string status, int processed = 0) =>
        new(jobId, Guid.NewGuid(), status, processed, 0, 0, 0,
            DateTimeOffset.UtcNow);

    [Fact]
    public void Snapshot_ReturnsLastEventPerJob()
    {
        var feed = new IngestionProgressFeed();
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        feed.Publish(Evt(jobA, "running", processed: 1));
        feed.Publish(Evt(jobA, "done", processed: 5));   // terminal wins
        feed.Publish(Evt(jobB, "failed"));               // terminal, no progress

        var snapshot = feed.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("done", snapshot.Single(e => e.JobId == jobA).Status);
        Assert.Equal("failed", snapshot.Single(e => e.JobId == jobB).Status);
    }

    [Fact]
    public void Snapshot_ThrottledRunningTick_StillTracked()
    {
        // The broadcast throttle must not starve the snapshot: a second
        // "running" tick inside the 1s window is dropped for subscribers but
        // still updates the per-job last state.
        var feed = new IngestionProgressFeed();
        var job = Guid.NewGuid();

        feed.Publish(Evt(job, "running", processed: 1));
        feed.Publish(Evt(job, "running", processed: 2)); // throttled for broadcast

        Assert.Equal(2, feed.Snapshot().Single(e => e.JobId == job).Processed);
    }

    [Fact]
    public void Snapshot_NoEvents_Empty()
    {
        Assert.Empty(new IngestionProgressFeed().Snapshot());
    }
}
