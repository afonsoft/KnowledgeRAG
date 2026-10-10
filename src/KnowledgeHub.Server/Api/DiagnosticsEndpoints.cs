using KnowledgeHub.Server.VectorStore;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20260925-pgvector-source-cascade RF-004: diagnostics endpoints —
/// vector store provider/size/index status for ops inspection.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static RouteGroupBuilder MapDiagnosticsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diagnostics");

        group.MapGet("/vectorstore", async (
            IVectorStore vectors, IConfiguration cfg,
            Data.KnowledgeHubDbContext db,
            Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
            Microsoft.Extensions.Caching.Distributed.IDistributedCache l2,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            // Ops-inspection read — the payload only changes with the index
            // contents, so key it on the index version token (every sync
            // re-keys automatically); JsonElement makes the provider-specific
            // shapes round-trippable through L2.
            var version = await Caching.IndexVersionToken.GetAsync(
                l2, lf.CreateLogger(typeof(Caching.IndexVersionToken)), ct);
            var payload = await Caching.EndpointCache.GetJsonAsync(cache,
                $"diagnostics:vectorstore:v{version}",
                async c => System.Text.Json.JsonSerializer.SerializeToElement(
                    await VectorStoreDiagnostics.BuildAsync(vectors, cfg, db, c)),
                lf, ct);
            return Results.Json(payload);
        });

        return group;
    }
}
