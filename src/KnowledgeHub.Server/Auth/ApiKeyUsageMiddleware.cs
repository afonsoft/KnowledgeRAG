using System.Collections.Concurrent;
using System.Diagnostics;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Auth;

/// <summary>
/// SPEC-20260915-apikey-usage-audit RF-002/RF-005: after the pipeline runs,
/// persists an <see cref="ApiKeyUsageEvent"/> for every request whose principal
/// carries <c>auth_method=apikey</c>. Records are best-effort — a write failure
/// logs a warning and never fails the request. Retention (90 days or 10_000
/// events per key, whichever evicts first) is pruned on insert — amortized:
/// a COUNT(*) probe gates the work, over-cap rows are dropped by a bounded
/// ORDER BY…LIMIT delete, and the 90-day sweep runs at most once per
/// <see cref="TimePruneInterval"/> per key.
/// </summary>
public sealed class ApiKeyUsageMiddleware(RequestDelegate next, ILogger<ApiKeyUsageMiddleware> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int MaxEventsPerKey = 10_000;

    /// <summary>Minimum interval between age-based (90d) sweeps per key.</summary>
    private static readonly TimeSpan TimePruneInterval = TimeSpan.FromMinutes(5);

    /// <summary>Last sweep per key (process-local; the key set is tiny).</summary>
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastSweepUtc = new();

    private const int MaxPathLength = 256;
    private const int MaxUserAgentLength = 200;

    public async Task InvokeAsync(HttpContext context, KnowledgeHubDbContext db)
    {
        var stopwatch = Stopwatch.StartNew();
        await next(context);
        stopwatch.Stop();

        if (context.User.FindFirst(ApiKeyAuthenticationHandler.AuthMethodClaim)?.Value != "apikey"
            || !Guid.TryParse(context.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value, out var keyId))
            return;

        try
        {
            var userAgent = context.Request.Headers.UserAgent.ToString();
            db.ApiKeyUsageEvents.Add(new ApiKeyUsageEvent
            {
                ApiKeyId = keyId,
                Timestamp = DateTimeOffset.UtcNow,
                HttpMethod = context.Request.Method,
                Path = Truncate(context.Request.Path.Value ?? "/", MaxPathLength),
                StatusCode = context.Response.StatusCode,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                UserAgent = string.IsNullOrEmpty(userAgent) ? null : Truncate(userAgent, MaxUserAgentLength)
            });

            // Audit write must not be cancelled with the request — the call
            // already happened and belongs in the log. RequestAborted may
            // already be cancelled by the time the pipeline returns.
            await PruneAsync(db, keyId, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record api key usage event for key {KeyId}", keyId);
        }
    }

    // SQLite cannot translate DateTimeOffset comparisons — the age cutoff is
    // still evaluated in memory, but bounded by the sweep throttle below.
    private static async Task PruneAsync(KnowledgeHubDbContext db, Guid keyId, CancellationToken ct)
    {
        // Perf pass (2026-10-03): materializing up to MaxEventsPerKey stamp
        // rows on EVERY apikey request made the audit itself the hot path. A
        // COUNT(*) probe (one index range scan) now gates the expensive part.
        var count = await db.ApiKeyUsageEvents.CountAsync(e => e.ApiKeyId == keyId, ct);
        var sweepDue = !LastSweepUtc.TryGetValue(keyId, out var last)
            || DateTimeOffset.UtcNow - last >= TimePruneInterval;
        if (count < MaxEventsPerKey && !sweepDue)
            return;

        // Over-cap: drop exactly the oldest (count - cap + 1) rows. Timestamp is
        // stored as ISO-8601 TEXT always in UTC ("+00:00"), so SQL ORDER BY is
        // chronological — LIMIT keeps it bounded, no 10k-row materialization.
        var overCap = count - (MaxEventsPerKey - 1);
        if (overCap > 0)
        {
            await db.Database.ExecuteSqlAsync($@"
                DELETE FROM ApiKeyUsageEvents WHERE Id IN (
                    SELECT Id FROM ApiKeyUsageEvents
                    WHERE ApiKeyId = {keyId}
                    ORDER BY Timestamp LIMIT {overCap})", ct);
        }

        if (!sweepDue)
            return;
        LastSweepUtc[keyId] = DateTimeOffset.UtcNow;

        // Age sweep — amortized to ≤1 run per key per TimePruneInterval.
        var stamps = await db.ApiKeyUsageEvents
            .Where(e => e.ApiKeyId == keyId)
            .Select(e => new { e.Id, e.Timestamp })
            .ToListAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - Retention;
        var removeIds = stamps.Where(e => e.Timestamp < cutoff).Select(e => e.Id).ToList();
        if (removeIds.Count == 0)
            return;
        await db.ApiKeyUsageEvents
            .Where(e => removeIds.Contains(e.Id))
            .ExecuteDeleteAsync(ct);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
