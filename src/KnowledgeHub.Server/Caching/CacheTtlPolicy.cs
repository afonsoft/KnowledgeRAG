using KnowledgeHub.Server.Configuration;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// SPEC-20260925-cache-region-ttl-policies RF-001: resolves the TTL for a cache
/// key from its region prefix (<c>emb:</c>, <c>search:</c>, <c>ans:</c>,
/// <c>mcp:tool:</c>, <c>rewrite:</c>, <c>index:</c>, …) using
/// <c>Cache:RegionTtlMinutes</c>. Longest-prefix match wins; unknown keys get
/// <c>DefaultTtlMinutes</c>. Singleton — the static <see cref="Current"/> lets
/// <see cref="SafeCache"/> (a static helper) reach it without changing every
/// call-site signature.
/// </summary>
public sealed class CacheTtlPolicy
{
    public static CacheTtlPolicy? Current { get; private set; }

    private readonly CacheOptions _options;

    public CacheTtlPolicy(IOptions<CacheOptions> options)
    {
        _options = options.Value;
        Current = this;
    }

    /// <summary>L1 lifetime ceiling for <see cref="EndpointCache"/> entries —
    /// <see cref="CacheOptions.L1MaxTtlMinutes"/>, or <see cref="TimeSpan.Zero"/>
    /// when <c>Cache:L1Enabled=false</c> (tells callers to disable the local tier).</summary>
    public TimeSpan L1Cap =>
        _options.L1Enabled ? TimeSpan.FromMinutes(_options.L1MaxTtlMinutes) : TimeSpan.Zero;

    /// <summary>TTL for a key. Keys embed the version token already, so the
    /// policy never needs invalidation semantics — just region length.</summary>
    public TimeSpan For(string key)
    {
        var bestLen = 0;
        var bestMinutes = _options.DefaultTtlMinutes;
        foreach (var (region, minutes) in _options.RegionTtlMinutes)
        {
            if (region.Length > bestLen
                && key.StartsWith(region + ":", StringComparison.Ordinal))
            {
                bestLen = region.Length;
                bestMinutes = minutes;
            }
        }
        return TimeSpan.FromMinutes(bestMinutes);
    }
}
