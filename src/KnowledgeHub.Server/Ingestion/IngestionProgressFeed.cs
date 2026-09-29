using System.Collections.Concurrent;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>One progress tick or terminal state of an ingestion job
/// (SPEC-20260925-job-progress-feed). Counters only — never doc content.</summary>
public sealed record IngestionProgressEvent(
    Guid JobId,
    Guid SourceId,
    string Status,
    int Processed,
    int Skipped,
    int Failed,
    int ChunksCreated,
    DateTimeOffset Timestamp);

/// <summary>Pub/sub channel for ingestion progress — fanned out to SignalR
/// clients by <see cref="Hubs.IngestionProgressBroadcastService"/>.</summary>
public interface IIngestionProgressFeed
{
    void Publish(IngestionProgressEvent e);
    event Action<IngestionProgressEvent>? Published;

    /// <summary>SPEC-20260929-observability-and-tests-residual RF-001: last
    /// event per job (terminal states retained briefly) so a client that
    /// connects after an orphan sweep still sees the terminal event.</summary>
    IReadOnlyList<IngestionProgressEvent> Snapshot();
}

/// <summary>Throttled in-memory feed: running-status events are capped at one
/// per second per job for the broadcast; the per-job snapshot tracks every
/// publish so late joiners replay the true last state.</summary>
public sealed class IngestionProgressFeed : IIngestionProgressFeed
{
    private const int MaxTrackedJobs = 256;

    // Terminal events stay visible to late joiners for a bounded window —
    // long enough to cover a client reconnect after a restart, short enough
    // that the map cannot grow on stale jobs forever.
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPublish = new();
    private readonly ConcurrentDictionary<Guid, (IngestionProgressEvent Event, DateTimeOffset StoredAt)> _lastEvent = new();

    public event Action<IngestionProgressEvent>? Published;

    public void Publish(IngestionProgressEvent e)
    {
        _lastEvent[e.JobId] = (e, DateTimeOffset.UtcNow);
        Prune();

        if (e.Status == "running")
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastPublish.TryGetValue(e.JobId, out var last)
                && now - last < TimeSpan.FromSeconds(1))
                return;
            _lastPublish[e.JobId] = now;
        }
        else
        {
            _lastPublish.TryRemove(e.JobId, out _);
        }
        Published?.Invoke(e);
    }

    public IReadOnlyList<IngestionProgressEvent> Snapshot() =>
        _lastEvent.Values
            .Select(v => v.Event)
            .OrderByDescending(e => e.Timestamp)
            .ToList();

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - TerminalRetention;
        foreach (var kv in _lastEvent)
            if (kv.Value.Event.Status != "running" && kv.Value.StoredAt < cutoff)
                _lastEvent.TryRemove(kv.Key, out _);

        if (_lastEvent.Count > MaxTrackedJobs)
            foreach (var kv in _lastEvent
                .OrderBy(kv => kv.Value.StoredAt)
                .Take(_lastEvent.Count - MaxTrackedJobs))
                _lastEvent.TryRemove(kv.Key, out _);
    }
}
