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
            await RunSearchAsync(svc, config,
                new SearchInvocation(q.Query, q.Mode, q.TopK, q.SourceId, BuildFilter(q)), ct));

        // SPEC-20260923-retrieval-quality §5: POST variant accepting a filters object.
        group.MapPost("/", async (
            ISearchService svc, IConfiguration config, SearchRequest request, CancellationToken ct) =>
            await RunSearchAsync(svc, config,
                new SearchInvocation(request.Query, request.Mode, request.TopK, request.SourceId, request.Filters), ct));

        return group;
    }

    /// <summary>Normalized handler inputs — the GET binds flat query params,
    /// the POST a filters object; both funnel through <see cref="RunSearchAsync"/>.</summary>
    private sealed record SearchInvocation(
        string? Query, string? Mode, int? TopK, Guid? SourceId, SearchFilter? Filters);

    // SPEC-20260923-retrieval-quality RF-003: flat filter params.
    private static SearchFilter BuildFilter(SearchQueryParams q) => new()
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
    };

    private static async Task<IResult> RunSearchAsync(
        ISearchService svc, IConfiguration config, SearchInvocation inv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inv.Query))
            return Results.BadRequest(new { error = "query is required" });

        // Default "semantic" preserves pre-hybrid API behavior; tools default to hybrid.
        var searchMode = ParseMode(inv.Mode);
        if (searchMode is null)
            return Results.BadRequest(new { error = "mode must be hybrid | semantic | lexical" });

        if (!Search.ResolvedSearchFilter.TryResolve(inv.Filters, out var filter, out var error))
            return Results.BadRequest(new { error });

        var k = inv.TopK is null or <= 0 ? DefaultTopK : Math.Min(inv.TopK.Value, MaxTopK);
        var results = await svc.SearchAsync(inv.Query, k, inv.SourceId, searchMode.Value, filter, ct: ct);
        return Results.Ok(Enrich(results, filter, inv.SourceId, config));
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
