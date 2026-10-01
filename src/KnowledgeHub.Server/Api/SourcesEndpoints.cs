using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>REST endpoints for knowledge-source CRUD + lifecycle (SPEC-02 RF-002/RF-003).</summary>
public static class SourcesEndpoints
{
    private const string SourceNotFound = "Source not found";

    public static RouteGroupBuilder MapSourcesApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources");

        group.MapGet("/", async (IKnowledgeSourceService svc, string? type, bool? active, CancellationToken ct) =>
        {
            SourceType? parsed = null;
            if (type is not null)
            {
                if (!Enum.TryParse<SourceType>(type, ignoreCase: true, out var t) || !Enum.IsDefined(t))
                    return Results.BadRequest(new { error = $"Invalid source type '{type}'" });
                parsed = t;
            }
            return Results.Ok(await svc.ListAsync(parsed, active, ct));
        });

        group.MapGet("/{id:guid}", async (IKnowledgeSourceService svc, Guid id, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound(new { error = SourceNotFound }));

        group.MapPost("/", async (IKnowledgeSourceService svc, CreateKnowledgeSourceRequest request, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(request, ct);
            return result.ErrorStatus is { } status
                ? Results.Json(new { error = result.Error }, statusCode: status)
                : Results.Created($"/api/sources/{result.Value!.Id}", result.Value);
        });

        group.MapPut("/{id:guid}", async (IKnowledgeSourceService svc, Guid id, UpdateKnowledgeSourceRequest request, CancellationToken ct) =>
            MapResult(await svc.UpdateAsync(id, request, ct)));

        group.MapDelete("/{id:guid}", async (IKnowledgeSourceService svc, Guid id, CancellationToken ct) =>
            MapResult(await svc.DeleteAsync(id, ct)));

        group.MapPost("/{id:guid}/activate", (IKnowledgeSourceService svc, Guid id, CancellationToken ct) =>
            SetActive(svc, id, true, ct));

        group.MapPost("/{id:guid}/deactivate", (IKnowledgeSourceService svc, Guid id, CancellationToken ct) =>
            SetActive(svc, id, false, ct));

        // SPEC-20260923-rate-limiting: sync burns embeddings — stricter bucket.
        // SPEC-20260924-async-ingestion-queue RF-001: default = enqueue + 202
        // jobId; ?wait=true keeps the legacy synchronous contract.
        group.MapPost("/{id:guid}/sync", async (
            IKnowledgeSourceService sources, IIngestionService ingestion,
            Ingestion.IIngestionQueue queue, Guid id, bool? wait, CancellationToken ct) =>
        {
            if (await sources.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = SourceNotFound });

            if (wait == true)
            {
                var result = await ingestion.SyncAsync(id, cancellationToken: ct);
                return Results.Accepted($"/api/sources/{id}", result);
            }

            try
            {
                var (job, existed) = await queue.EnqueueAsync(id, "sync", ct);
                return Results.Accepted($"/api/ingestion/jobs/{job.Id}",
                    new { jobId = job.Id, status = job.Status, existing = existed });
            }
            catch (Ingestion.QueueFullException)
            {
                return Results.Problem(
                    "ingestion queue is full — try again later",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting("sync");

        // RF-003: force re-chunk + re-embed regardless of content hash.
        group.MapPost("/{id:guid}/reindex", async (
            IKnowledgeSourceService sources, Ingestion.IIngestionQueue queue,
            Guid id, CancellationToken ct) =>
        {
            if (await sources.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = SourceNotFound });
            try
            {
                var (job, existed) = await queue.EnqueueAsync(id, "reindex", ct);
                return Results.Accepted($"/api/ingestion/jobs/{job.Id}",
                    new { jobId = job.Id, status = job.Status, existing = existed });
            }
            catch (Ingestion.QueueFullException)
            {
                return Results.Problem(
                    "ingestion queue is full — try again later",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting("sync");

        group.MapGet("/{id:guid}/documents", async (IKnowledgeSourceService svc, Guid id, CancellationToken ct) =>
            await svc.ListDocumentsAsync(id, ct) is { } docs ? Results.Ok(docs) : Results.NotFound(new { error = SourceNotFound }));

        return group;
    }

    private static async Task<IResult> SetActive(IKnowledgeSourceService svc, Guid id, bool active, CancellationToken ct) =>
        MapResult(await svc.SetActiveAsync(id, active, ct));

    private static IResult MapResult<T>(ServiceResult<T> result) =>
        result.ErrorStatus is { } status
            ? Results.Json(new { error = result.Error }, statusCode: status)
            : Results.Ok(result.Value);
}
