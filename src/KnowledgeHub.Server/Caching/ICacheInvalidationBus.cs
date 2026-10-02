using StackExchange.Redis;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// SPEC-20260925-distributed-invalidation-pubsub RF-001: fire-and-forget
/// invalidation between replicas — publishers broadcast a topic, every OTHER
/// instance evicts its local L1 accordingly. No-op under provider=memory.
/// </summary>
public interface ICacheInvalidationBus
{
    /// <summary>Broadcasts an invalidation topic. Topics:
    /// <c>index-version</c> (token bumped — drop it locally so the next read
    /// re-fetches), <c>cache-clear</c> (admin wiped the cache — drop all L1),
    /// <c>cache-key:{key}</c> (one entry evicted — drop its L1 copy;
    /// SPEC-20260926-cache-key-consistency RF-001).</summary>
    Task PublishAsync(string topic, CancellationToken ct = default);

    /// <summary>Raised on this instance when ANOTHER replica published. The
    /// publisher instance is never notified (originInstanceId check).</summary>
    event EventHandler<string>? Received;
}

/// <summary>Single-replica / memory provider — nothing to propagate.</summary>
public sealed class NoopInvalidationBus : ICacheInvalidationBus
{
    public event EventHandler<string>? Received { add { /* no-op bus */ } remove { /* no-op bus */ } }
    public Task PublishAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Redis pub/sub transport on channel <c>kh:invalidate</c>.
/// Wire format: <c>{instanceId}|{topic}</c> — origin skips its own echo.</summary>
public sealed class RedisInvalidationBus : ICacheInvalidationBus
{
    private const string Channel = "kh:invalidate";
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisInvalidationBus> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    public event EventHandler<string>? Received;

    private int _subscribed;

    public RedisInvalidationBus(IConnectionMultiplexer redis, ILogger<RedisInvalidationBus> logger)
    {
        _redis = redis;
        _logger = logger;
        // RF-003 (SPEC-20260926-cache-coherence-and-ttl): subscribe must not
        // crash startup when Redis is briefly unavailable — retry on reconnect.
        TrySubscribe();
        // RF-302 (SPEC-20260926-review-backlog-remediation): the multiplexer
        // RESTORES subscriptions itself after a reconnect — re-subscribing on
        // every ConnectionRestored stacked duplicate handlers and each
        // cache-clear ran N times. Retry only when no subscribe ever landed.
        _redis.ConnectionRestored += (_, _) => TrySubscribe();
    }

    private void TrySubscribe()
    {
        if (Interlocked.CompareExchange(ref _subscribed, 1, 0) != 0)
            return;
        try
        {
            var sub = _redis.GetSubscriber();
            sub.Subscribe(RedisChannel.Literal(Channel), (_, msg) =>
            {
                var parts = ((string?)msg)?.Split('|', 2);
                if (parts is not [var origin, var topic] || origin == _instanceId)
                    return;
                try { Received?.Invoke(this, topic); }
                catch (Exception ex) { _logger.LogWarning(ex, "invalidation handler failed for {Topic}", LogSafe(topic)); }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _subscribed, 0);
            _logger.LogWarning(ex,
                "invalidation subscribe failed — degraded to no-propagation until reconnect");
        }
    }

    public async Task PublishAsync(string topic, CancellationToken ct = default)
    {
        try
        {
            await _redis.GetSubscriber()
                .PublishAsync(RedisChannel.Literal(Channel), $"{_instanceId}|{topic}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "publish invalidation {Topic} failed — other replicas keep stale L1 until TTL", LogSafe(topic));
        }
    }

    // topic can carry channel-originated content — strip line breaks before
    // it reaches the rendered log message (log forging).
    private static string LogSafe(string value) => value.ReplaceLineEndings("_");
}
