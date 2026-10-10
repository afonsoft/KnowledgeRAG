using System.Text.Json.Serialization;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>RAG Quality Triad stats (SPEC-20260927-rag-evaluation-triad-metrics §5).</summary>
public sealed record RagEvaluationStats(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("totalEvaluations")] int TotalEvaluations,
    [property: JsonPropertyName("averageContextRelevance")] double AverageContextRelevance,
    [property: JsonPropertyName("averageGroundedness")] double AverageGroundedness,
    [property: JsonPropertyName("averageAnswerRelevance")] double AverageAnswerRelevance,
    [property: JsonPropertyName("hallucinationRatePercent")] double HallucinationRatePercent,
    [property: JsonPropertyName("recentFlaggedQueries")] IReadOnlyList<FlaggedQuery> RecentFlaggedQueries);

/// <summary>One flagged evaluation for the dashboard.</summary>
public sealed record FlaggedQuery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("groundednessScore")] double GroundednessScore,
    [property: JsonPropertyName("flaggedAt")] DateTimeOffset FlaggedAt);

/// <summary>Minimal API for the RAG Quality Triad dashboard.</summary>
public static class RagEvaluationEndpoints
{
    public static IEndpointConventionBuilder MapRagEvaluationApi(this IEndpointRouteBuilder app)
    {
        // Dashboard polling endpoint — the aggregation is identical for every
        // admin viewer, so the built stats are cached briefly (eval region TTL)
        // instead of re-scanning RagEvaluations on every render.
        return app.MapGet("/api/v1/evaluation/stats", async (
            KnowledgeHubDbContext db,
            Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var stats = await Caching.EndpointCache.GetJsonAsync(cache, "eval:stats",
                async c => await BuildStatsAsync(db, c), lf, ct);
            return Results.Ok(stats);
        });
    }

    private static async Task<RagEvaluationStats> BuildStatsAsync(
        KnowledgeHubDbContext db, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);

        // SQLite cannot translate DateTimeOffset comparisons — materialize and
        // filter in memory (same pattern as the api-key audit prune). The table
        // is bounded by the worker's retention prune, so the scan stays small.
        // The DbContext is the shared scoped instance — ApiKeyUsageMiddleware
        // writes the audit event on it after this handler returns, so it must
        // NOT be disposed here.
        var rows = (await db.RagEvaluations.AsNoTracking().ToListAsync(ct))
            .Where(r => r.TimestampUtc >= since)
            .ToList();

        var total = rows.Count;
        return new RagEvaluationStats(
            Period: "Last7Days",
            TotalEvaluations: total,
            AverageContextRelevance: total == 0 ? 0 : rows.Average(r => r.ContextRelevance),
            AverageGroundedness: total == 0 ? 0 : rows.Average(r => r.Groundedness),
            AverageAnswerRelevance: total == 0 ? 0 : rows.Average(r => r.AnswerRelevance),
            HallucinationRatePercent: total == 0 ? 0 : Math.Round(rows.Count(r => r.FlaggedAsHallucination) * 100.0 / total, 1),
            RecentFlaggedQueries: rows
                .Where(r => r.FlaggedAsHallucination)
                .OrderByDescending(r => r.TimestampUtc)
                .Take(10)
                .Select(r => new FlaggedQuery(
                    r.Id.ToString("N"),
                    r.Question,
                    Math.Round(r.Groundedness, 2),
                    r.TimestampUtc))
                .ToList());
    }
}
