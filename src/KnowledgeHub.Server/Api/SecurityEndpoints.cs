using KnowledgeHub.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20260923-prompt-injection-guard §5: GET /api/security/events lists
/// recent injection-scan detections (ids + flags only — never content).
/// </summary>
public static class SecurityEndpoints
{
    public static RouteGroupBuilder MapSecurityApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/security");

        group.MapGet("/events", async (KnowledgeHubDbContext db, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            // SQLite cannot ORDER BY DateTimeOffset — sort in memory.
            var events = (await db.SecurityEvents.AsNoTracking()
                    .Select(e => new { e.Id, e.SourceId, e.DocumentId, e.ChunkIndex, e.Flags, e.ApiKeyId, e.Detail, e.CreatedAt })
                    .ToListAsync(ct))
                .OrderByDescending(e => e.CreatedAt)
                .Take(take);
            return Results.Ok(events);
        });

        // SPEC-20261111-memory-defense RF-005: scan policy status — pattern
        // names + actions only, never values or spans.
        group.MapGet("/secret-scan/status", (
            Security.ISecretScanner scanner, Security.DefensePolicyResolver resolver) =>
        {
            var global = resolver.Global();
            return Results.Ok(new
            {
                enabled = global.Enabled,
                defaultAction = global.DefaultAction.ToString().ToLowerInvariant(),
                patternCount = scanner.Patterns.Count,
                patterns = scanner.Patterns.OrderBy(p => p, StringComparer.Ordinal)
                    .Select(p => new { name = p, action = global.ActionFor(p).ToString().ToLowerInvariant() })
            });
        });

        return group;
    }
}
