using KnowledgeHub.Server.Data;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// Read-only REST mirror of the temporal/episodic knowledge graph for the
/// /graph page (SPEC-20260928-graph-timeline-viewer RF-001): light projections
/// over <c>KgNodes</c>/<c>KgEdges</c>/<c>KgEpisodes</c>. Policy:
/// <c>CookieSession</c> (UI-only surface).
/// </summary>
public static class GraphEndpoints
{
    private const int MaxTake = 200;

    public static RouteGroupBuilder MapGraphApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/graph");

        group.MapGet("/nodes", async (
            KnowledgeHubDbContext db,
            int? skip, int? take, bool? includeHistorical,
            CancellationToken ct) =>
        {
            var s = Math.Max(0, skip ?? 0);
            var t = Math.Clamp(take ?? 100, 1, MaxTake);
            var query = db.KgNodes.AsNoTracking().Include(n => n.Episode).AsQueryable();
            if (includeHistorical != true)
                query = query.Where(n => n.ValidTo == null);

            var total = await query.CountAsync(ct);
            var nodes = await query
                .OrderByDescending(n => n.ObservedAt)
                .Skip(s).Take(t)
                .ToListAsync(ct);
            return Results.Ok(new GraphNodesResponse
            {
                Total = total,
                Nodes = nodes.Select(ToDto).ToList()
            });
        });

        group.MapGet("/nodes/{id:guid}/edges", async (
            Guid id, KnowledgeHubDbContext db, bool? includeHistorical,
            CancellationToken ct) =>
        {
            var query = db.KgEdges.AsNoTracking()
                .Include(e => e.From).Include(e => e.To)
                .Where(e => e.FromNodeId == id || e.ToNodeId == id);
            if (includeHistorical != true)
                query = query.Where(e => e.ValidTo == null);

            var edges = await query
                .OrderByDescending(e => e.ObservedAt)
                .Take(MaxTake)
                .ToListAsync(ct);
            return Results.Ok(edges.Select(ToDto).ToList());
        });

        group.MapGet("/episodes", async (
            KnowledgeHubDbContext db, int? take, CancellationToken ct) =>
        {
            var t = Math.Clamp(take ?? 100, 1, MaxTake);
            var rows = await db.KgEpisodes.AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .Take(t)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(e => new GraphEpisodeDto
            {
                Id = e.Id.ToString("N"),
                Kind = e.Kind,
                KnowledgeSourceId = e.KnowledgeSourceId?.ToString("N"),
                Summary = e.Summary,
                CreatedAt = e.CreatedAt,
                EndedAt = e.EndedAt
            }).ToList());
        });

        group.MapGet("/timeline", async (
            KnowledgeHubDbContext db,
            DateTime? from, DateTime? to, int? take, bool? includeHistorical,
            CancellationToken ct) =>
        {
            var t = Math.Clamp(take ?? 100, 1, MaxTake);
            var query = db.KgNodes.AsNoTracking().Include(n => n.Episode).AsQueryable();
            if (includeHistorical != true)
                query = query.Where(n => n.ValidTo == null);
            // A node matches the window when its validity interval intersects
            // [from,to] — ValidFrom <= to AND (ValidTo null OR ValidTo >= from).
            if (from is { } f)
                query = query.Where(n => n.ValidTo == null || n.ValidTo >= f);
            if (to is { } tt)
                query = query.Where(n => n.ValidFrom <= tt);

            var nodes = await query
                .OrderByDescending(n => n.ObservedAt)
                .Take(t)
                .ToListAsync(ct);
            return Results.Ok(nodes.Select(ToDto).ToList());
        });

        return group;
    }

    private static GraphNodeDto ToDto(Domain.Entities.KgNode n) => new()
    {
        Id = n.Id.ToString("N"),
        Name = n.Name,
        Type = n.Type,
        Labels = n.Labels,
        FirstSeenAt = n.FirstSeenAt,
        ObservedAt = n.ObservedAt,
        ValidFrom = n.ValidFrom,
        ValidTo = n.ValidTo,
        EpisodeId = n.EpisodeId?.ToString("N"),
        EpisodeSummary = n.Episode?.Summary
    };

    private static GraphEdgeDto ToDto(Domain.Entities.KgEdge e) => new()
    {
        Id = e.Id.ToString("N"),
        FromNodeId = e.FromNodeId.ToString("N"),
        FromName = e.From.Name,
        ToNodeId = e.ToNodeId.ToString("N"),
        ToName = e.To.Name,
        Kind = e.Kind,
        Weight = e.Weight,
        ObservedAt = e.ObservedAt,
        ValidFrom = e.ValidFrom,
        ValidTo = e.ValidTo,
        EpisodeId = e.EpisodeId?.ToString("N"),
        EvidenceChunkId = e.EvidenceChunkId.ToString("N"),
        DocumentId = e.KnowledgeDocumentId.ToString("N")
    };
}
