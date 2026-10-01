using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.RateLimiting;

/// <summary>
/// SPEC-20260923-rate-limiting RF-003: in-dispatch limiter for MCP
/// <c>tools/call</c>. JSON-RPC has no 429 — over-limit calls get a friendly
/// <c>isError</c> result instead of an exception. LLM-spending and
/// write/sync tools are charged; read-only utility tools pass through.
/// </summary>
public sealed class McpToolRateLimiter : IDisposable
{
    private static readonly HashSet<string> LlmTools = new(StringComparer.Ordinal)
        { "ask_knowledge", "agent_chat", "search_knowledge" };

    private static readonly HashSet<string> SyncTools = new(StringComparer.Ordinal)
        { "write_knowledge", "write_note" };

    private readonly RateLimitOptions _options;
    private readonly IApiKeyRateLimitResolver _overrides;
    private readonly ILogger<McpToolRateLimiter> _logger;
    private readonly PartitionedRateLimiter<string> _llm;
    private readonly PartitionedRateLimiter<string> _sync;

    public McpToolRateLimiter(RateLimitOptions options, ILogger<McpToolRateLimiter> logger)
        : this(options, new NoOpApiKeyRateLimitResolver(), logger)
    {
    }

    public McpToolRateLimiter(
        RateLimitOptions options, IApiKeyRateLimitResolver overrides, ILogger<McpToolRateLimiter> logger)
    {
        _options = options;
        _overrides = overrides;
        _logger = logger;
        _llm = Build(options.LlmPermitLimit, options.AnonymousLlmPermitLimit, options.LlmWindowSeconds, sliding: true,
            o => (o.LlmPermits, o.LlmWindowSeconds));
        _sync = Build(options.SyncPermitLimit, options.SyncPermitLimit, options.SyncWindowSeconds, sliding: false,
            o => (o.SyncPermits, o.SyncWindowSeconds));
    }

    /// <summary>
    /// Attempts to charge one call. Returns true when the call may proceed;
    /// on rejection <paramref name="retryAfterSeconds"/> carries the hint.
    /// </summary>
    public bool TryAcquire(string toolName, HttpContext? http, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_options.Enabled)
            return true;

        var limiter = _sync;
        if (LlmTools.Contains(toolName))
            limiter = _llm;
        else if (!SyncTools.Contains(toolName))
            limiter = null;
        if (limiter is null)
            return true;

        var (key, kind, _) = CallerPartitioner.Resolve(http, _options.TrustForwardedHeaders);
        // RF-003/RF-005: the partition key carries an override fingerprint so
        // editing/clearing an override yields a fresh limiter — partitioned
        // limiters never rebuild an existing partition's options.
        key = WithOverrideFingerprint(key);
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
            return true;

        retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : _options.LlmWindowSeconds;

        _logger.LogWarning(
            "MCP tool {ToolName} rate limited for {Kind} partition (retry in {RetryAfter}s)",
            toolName, kind, retryAfterSeconds);
        return false;
    }

    /// <summary>Acrescenta um fingerprint dos valores de override à partition key
    /// — mesma key + override diferente = bucket diferente.</summary>
    private string WithOverrideFingerprint(string key)
    {
        if (!key.StartsWith("key:", StringComparison.Ordinal)
            || !Guid.TryParse(key.AsSpan(4), out var keyId)
            || !_overrides.TryGetOverride(keyId, out var ov) || ov is null)
            return key;
        return $"{key}:{ov.LlmPermits}/{ov.LlmWindowSeconds}/{ov.SyncPermits}/{ov.SyncWindowSeconds}";
    }

    /// <summary>Resolver vazio usado pelo construtor legado (testes, hosts sem o serviço).</summary>
    private sealed class NoOpApiKeyRateLimitResolver : IApiKeyRateLimitResolver
    {
        public bool TryGetOverride(Guid keyId, out ApiKeyRateLimitOverride? value)
        {
            value = null;
            return false;
        }
        public void Invalidate() { }
    }

    private PartitionedRateLimiter<string> Build(
        int permit, int anonPermit, int windowSeconds, bool sliding,
        Func<ApiKeyRateLimitOverride, (int? Permits, int? WindowSeconds)> selector) =>
        PartitionedRateLimiter.Create<string, string>(key =>
        {
            var limit = key.StartsWith(CallerPartitioner.AnonymousPrefix, StringComparison.Ordinal)
                ? anonPermit
                : permit;
            var window = windowSeconds;
            // SPEC-20260923-per-key-rate-limits RF-003: per-key override mixes
            // per-field with the global values. The partition key may carry a
            // `:{fingerprint}` suffix — the Guid is always chars 4..39.
            if (key.StartsWith("key:", StringComparison.Ordinal)
                && key.Length >= 40
                && Guid.TryParse(key.AsSpan(4, 36), out var keyId)
                && _overrides.TryGetOverride(keyId, out var ov) && ov is not null)
            {
                var (p, w) = selector(ov);
                limit = p ?? limit;
                window = w ?? window;
            }
            return sliding
                ? RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromSeconds(window),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0
                })
                : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromSeconds(window),
                    QueueLimit = 0
                });
        });

    public void Dispose()
    {
        _llm.Dispose();
        _sync.Dispose();
    }
}
