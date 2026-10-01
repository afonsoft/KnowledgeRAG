using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Server.Api;

/// <summary>Unified semantic search endpoint (SPEC-02 RF-004).</summary>
public static class SearchEndpoints
{
    /// <summary>Flat query-string contract for GET /api/search — grouped via
    /// [AsParameters] to keep the handler under the 7-parameter guideline (S107).</summary>
    public sealed class SearchQueryParams
    {
        public string? Query { get; init; }
        public int? TopK { get; init; }
        public Guid? SourceId { get; init; }
        public string? Mode { get; init; }
        public string? SourceType { get; init; }
        public string? PathPrefix { get; init; }
        public string? IndexedAfter { get; init; }
        public string? Language { get; init; }
        public int? WindowSize { get; init; }
        public string? LimitMode { get; init; }
        public int? AutocutSensitivity { get; init; }
        public string[]? SubQueries { get; init; }
        public bool? AllowRelaxation { get; init; }
    }

    public const int DefaultTopK = 5;
    public const int MaxTopK = 50;

    public static RouteGroupBuilder MapSearchApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search");

        group.MapGet("/", async (
            ISearchService svc, IConfiguration config,
            [AsParameters] SearchQueryParams q,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q.Query))
                return Results.BadRequest(new { error = "query is required" });

            // Default "semantic" preserves pre-hybrid API behavior; tools default to hybrid.
            var searchMode = ParseMode(q.Mode);
            if (searchMode is null)
                return Results.BadRequest(new { error = "mode must be hybrid | semantic | lexical" });

            // SPEC-20260923-retrieval-quality RF-003: flat filter params.
            if (!Search.ResolvedSearchFilter.TryResolve(
                    new SearchFilter
                    {
                        SourceType = q.SourceType,
                        PathPrefix = q.PathPrefix,
                        IndexedAfter = q.IndexedAfter,
                        Language = q.Language,
                        WindowSize = q.WindowSize,
                        LimitMode = q.LimitMode,
                        AutocutSensitivity = q.AutocutSensitivity,
                        SubQueries = q.SubQueries,
                        AllowRelaxation = q.AllowRelaxation
                    }, out var filter, out var error))
                return Results.BadRequest(new { error });

            var k = q.TopK is null or <= 0 ? DefaultTopK : Math.Min(q.TopK.Value, MaxTopK);
            var results = await svc.SearchAsync(q.Query, k, q.SourceId, searchMode.Value, filter, ct: ct);
            return Results.Ok(Enrich(results, filter, q.SourceId, config));
        });

        // SPEC-20260923-retrieval-quality §5: POST variant accepting a filters object.
        group.MapPost("/", async (
            ISearchService svc, IConfiguration config, SearchRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "query is required" });

            var searchMode = ParseMode(request.Mode);
            if (searchMode is null)
                return Results.BadRequest(new { error = "mode must be hybrid | semantic | lexical" });

            if (!Search.ResolvedSearchFilter.TryResolve(request.Filters, out var filter, out var error))
                return Results.BadRequest(new { error });

            var k = request.TopK is null or <= 0 ? DefaultTopK : Math.Min(request.TopK.Value, MaxTopK);
            var results = await svc.SearchAsync(request.Query, k, request.SourceId, searchMode.Value, filter, ct: ct);
            return Results.Ok(Enrich(results, filter, request.SourceId, config));
        });

        return group;
    }

    /// <summary>Envelope metadata: limit mode + relaxation provenance
    /// (SPEC-20260927 RF-003 — never relax silently).</summary>
    private static SearchResponse Enrich(
        IReadOnlyList<SearchResultItem> results, Search.ResolvedSearchFilter filter,
        Guid? sourceId, IConfiguration config) => new()
        {
            Results = results,
            TotalMatches = results.Count,
            LimitModeApplied = filter.EffectiveLimitMode(config),
            FilterRelaxed = results.Any(r => r.IsRelaxed),
            OriginalFilter = Search.ResolvedSearchFilter.DescribeScope(sourceId, filter),
            AppliedFilter = results.FirstOrDefault(r => r.IsRelaxed)?.RelaxedScope
                ?? Search.ResolvedSearchFilter.DescribeScope(sourceId, filter)
        };

    internal static SearchMode? ParseMode(string? mode) => mode switch
    {
        null or "" => SearchMode.Semantic,
        _ when Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed) => parsed,
        _ => null
    };
}
