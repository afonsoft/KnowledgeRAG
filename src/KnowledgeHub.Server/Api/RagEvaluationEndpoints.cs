using System.Text.Json.Serialization;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        return app.MapGet("/api/v1/evaluation/stats", async (IServiceProvider sp, CancellationToken ct) =>
        {
            await using var db = sp.GetRequiredService<KnowledgeHubDbContext>();
            var since = DateTimeOffset.UtcNow.AddDays(-7);

            var rows = await db.RagEvaluations
                .Where(r => r.TimestampUtc >= since)
                .ToListAsync(ct);

            var total = rows.Count;
            var stats = new RagEvaluationStats(
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

            return Results.Ok(stats);
        });
    }
}
