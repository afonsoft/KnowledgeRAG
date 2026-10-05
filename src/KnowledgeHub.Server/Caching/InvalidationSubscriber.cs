namespace KnowledgeHub.Server.Caching;

/// <summary>
/// SPEC-20260925-distributed-invalidation-pubsub RF-003: translates bus events
/// into local L1 evictions. <c>index-version</c> drops just the token key —
/// every cached result/answer key embeds it, so the next read re-fetches the
/// fresh token and all stale entries miss. <c>cache-clear</c> wipes L1.
/// <c>cache-key:{key}</c>/<c>cache-tag:{tag}</c> also drop the HybridCache L1 —
/// HybridCache's own invalidation covers the shared L2, never remote L1s.
/// </summary>
public sealed class InvalidationSubscriber : BackgroundService
{
    private const string CacheKeyTopicPrefix = "cache-key:";
    private const string CacheTagTopicPrefix = "cache-tag:";
    private readonly ICacheInvalidationBus _bus;
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache _cache;
    private readonly ICacheManagerService? _manager;
    private readonly Settings.IEmbeddingSettingsService? _embeddingSettings;
    private readonly Microsoft.Extensions.Caching.Hybrid.HybridCache? _hybrid;
    private readonly ILogger<InvalidationSubscriber> _logger;

    public InvalidationSubscriber(
        ICacheInvalidationBus bus,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ILogger<InvalidationSubscriber> logger,
        ICacheManagerService? manager = null,
        Settings.IEmbeddingSettingsService? embeddingSettings = null,
        Microsoft.Extensions.Caching.Hybrid.HybridCache? hybrid = null)
    {
        _bus = bus;
        _cache = cache;
        _manager = manager;
        _embeddingSettings = embeddingSettings;
        _hybrid = hybrid;
        _logger = logger;
        _bus.Received += OnReceived;
    }

    private void OnReceived(object? sender, string topic)
    {
        var l1 = _cache as L1L2Cache;

        // RF-301 (SPEC-20260926-review-backlog-remediation): the tracked-L2
        // cleanup must run regardless of cache TYPE — with L1Enabled=false the
        // cache is Redis-backed and the early return left replica keys alive.
        if (topic == "cache-clear" && _manager is not null)
            _ = ClearTrackedSafeAsync();

        // RF-606: another replica changed embedding settings — drop this
        // instance's snapshot so the next resolve re-reads its local store
        // and operators see a staleness signal.
        if (topic == "settings-changed" && _embeddingSettings is not null)
        {
            _embeddingSettings.Invalidate();
            _logger.LogInformation("settings-changed on another replica — embedding settings snapshot invalidated");
        }

        // HybridCache L1 drops for endpoint-level entries — RemoveByTagAsync
        // on the publisher already wrote the L2 tombstone; these calls only
        // need to clear THIS replica's local tier.
        if (_hybrid is not null)
        {
            if (topic.StartsWith(CacheTagTopicPrefix, StringComparison.Ordinal))
                _ = _hybrid.RemoveByTagAsync(topic[CacheTagTopicPrefix.Length..]);
            else if (topic.StartsWith(CacheKeyTopicPrefix, StringComparison.Ordinal))
                _ = _hybrid.RemoveAsync(topic[CacheKeyTopicPrefix.Length..]);
        }

        if (l1 is null)
            return; // no local tier to invalidate (memory provider: single process)

        switch (topic)
        {
            case "index-version":
                l1.InvalidateLocal(CacheKeys.IndexVersion);
                _logger.LogDebug("remote index-version bump — L1 token evicted");
                break;
            case "cache-clear":
                l1.InvalidateAllLocal();
                _logger.LogInformation("remote cache-clear — L1 compacted, tracked L2 keys dropping");
                break;
            default:
                // SPEC-20260926-cache-key-consistency RF-001: per-key eviction
                // propagated from another replica — drop only the L1 copy.
                if (topic.StartsWith(CacheKeyTopicPrefix, StringComparison.Ordinal))
                {
                    var key = topic[CacheKeyTopicPrefix.Length..];
                    l1.InvalidateLocal(key);
                    // The publisher already deleted the shared entry — the key
                    // is gone globally, so it leaves this replica's registry too.
                    _manager?.RemoveKey(key);
                    _logger.LogDebug("remote cache-key eviction — L1 entry dropped");
                }
                break;
        }
    }

    private async Task ClearTrackedSafeAsync()
    {
        try { await _manager!.ClearLocalTrackedAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "remote-clear: tracked L2 cleanup failed"); }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Delay(Timeout.Infinite, stoppingToken); // event-driven only

    public override void Dispose()
    {
        _bus.Received -= OnReceived;
        base.Dispose();
    }
}
