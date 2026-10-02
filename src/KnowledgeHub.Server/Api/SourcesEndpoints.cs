using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Caching.Hybrid;

namespace KnowledgeHub.Server.Api;

/// <summary>REST endpoints for knowledge-source CRUD + lifecycle (SPEC-02 RF-002/RF-003).
/// Reads ride the shared <c>list:sources</c> HybridCache tag — any mutation evicts the scope
/// so every replica's parameterized entries miss on the next read.</summary>
public static class SourcesEndpoints
{
    private const string SourceNotFound = "Source not found";
    internal const string ListScope = "list:sources";

    public static RouteGroupBuilder MapSourcesApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources");

        MapSourceCrudEndpoints(group);
        MapSourceLifecycleEndpoints(group);
        MapSourceJobEndpoints(group);

        return group;
    }

    private static void MapSourceCrudEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/", async (IKnowledgeSourceService svc, string? type, bool? active,
            HybridCache cache, ILoggerFactory lf, CancellationToken ct) =>
        {
            SourceType? parsed = null;
            if (type is not null)
            {
                if (!Enum.TryParse<SourceType>(type, ignoreCase: true, out var t) || !Enum.IsDefined(t))
                    return Results.BadRequest(new { error = $"Invalid source type '{type}'" });
                parsed = t;
            }
            var list = await EndpointCache.GetJsonAsync(cache, $"{ListScope}:{parsed}:{active}",
                async c => await svc.ListAsync(parsed, active, c), lf, ct, tags: [ListScope]);
            return Results.Ok(list ?? []);
        });

        group.MapGet("/{id:guid}", async (IKnowledgeSourceService svc, Guid id,
            HybridCache cache, ILoggerFactory lf, CancellationToken ct) =>
            await EndpointCache.GetJsonAsync(cache, $"{ListScope}:id:{id}",
                async c => await svc.GetAsync(id, c), lf, ct, tags: [ListScope]) is { } dto
                ? Results.Ok(dto)
                : Results.NotFound(new { error = SourceNotFound }));

        group.MapPost("/", async (IKnowledgeSourceService svc, CreateKnowledgeSourceRequest request,
            HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(request, ct);
            if (result.ErrorStatus is { } status)
                return Results.Json(new { error = result.Error }, statusCode: status);
            await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
            return Results.Created($"/api/sources/{result.Value!.Id}", result.Value);
        });

        group.MapPut("/{id:guid}", async (IKnowledgeSourceService svc, Guid id, UpdateKnowledgeSourceRequest request,
            HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct) =>
            await MapResultAsync(svc.UpdateAsync(id, request, ct), cache, bus, lf, ct));

        group.MapDelete("/{id:guid}", async (IKnowledgeSourceService svc, Guid id,
            HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct) =>
            await MapResultAsync(svc.DeleteAsync(id, ct), cache, bus, lf, ct));
    }

    private static void MapSourceLifecycleEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/activate", (IKnowledgeSourceService svc, Guid id,
            HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct) =>
            SetActive(svc, id, true, cache, bus, lf, ct));

        group.MapPost("/{id:guid}/deactivate", (IKnowledgeSourceService svc, Guid id,
            HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct) =>
            SetActive(svc, id, false, cache, bus, lf, ct));

        group.MapGet("/{id:guid}/documents", async (IKnowledgeSourceService svc, Guid id,
            HybridCache cache, ILoggerFactory lf, CancellationToken ct) =>
            await EndpointCache.GetJsonAsync(cache, $"{ListScope}:docs:{id}",
                async c => await svc.ListDocumentsAsync(id, c), lf, ct, tags: [ListScope]) is { } docs
                ? Results.Ok(docs)
                : Results.NotFound(new { error = SourceNotFound }));
    }

    private static void MapSourceJobEndpoints(RouteGroupBuilder group)
    {
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

            return await EnqueueJobAsync(queue, id, "sync", ct);
        }).RequireRateLimiting("sync");

        // RF-003: force re-chunk + re-embed regardless of content hash.
        group.MapPost("/{id:guid}/reindex", async (
            IKnowledgeSourceService sources, Ingestion.IIngestionQueue queue,
            Guid id, CancellationToken ct) =>
        {
            if (await sources.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = SourceNotFound });
            return await EnqueueJobAsync(queue, id, "reindex", ct);
        }).RequireRateLimiting("sync");
    }

    private static async Task<IResult> EnqueueJobAsync(
        Ingestion.IIngestionQueue queue, Guid id, string jobType, CancellationToken ct)
    {
        try
        {
            var (job, existed) = await queue.EnqueueAsync(id, jobType, ct);
            return Results.Accepted($"/api/ingestion/jobs/{job.Id}",
                new { jobId = job.Id, status = job.Status, existing = existed });
        }
        catch (Ingestion.QueueFullException)
        {
            return Results.Problem(
                "ingestion queue is full — try again later",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> SetActive(IKnowledgeSourceService svc, Guid id, bool active,
        HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct) =>
        MapResultAsync(svc.SetActiveAsync(id, active, ct), cache, bus, lf, ct);

    private static async Task<IResult> MapResultAsync<T>(Task<ServiceResult<T>> resultTask,
        HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct)
    {
        var result = await resultTask;
        if (result.ErrorStatus is { } status)
            return Results.Json(new { error = result.Error }, statusCode: status);
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
        return Results.Ok(result.Value);
    }
}
