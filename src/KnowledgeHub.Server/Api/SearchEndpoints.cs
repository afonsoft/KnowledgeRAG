using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Server.Api;

/// <summary>Unified semantic search endpoint (SPEC-02 RF-004).</summary>
public static class SearchEndpoints
{
    public const int DefaultTopK = 5;
    public const int MaxTopK = 50;

    public static RouteGroupBuilder MapSearchApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search");

        group.MapGet("/", async (
            ISearchService svc, IConfiguration config, string? query, int? topK, Guid? sourceId,
            string? mode, string? sourceType, string? pathPrefix, string? indexedAfter,
            string? language, int? windowSize, string? limitMode, int? autocutSensitivity,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "query is required" });

            // Default "semantic" preserves pre-hybrid API behavior; tools default to hybrid.
            var searchMode = ParseMode(mode);
            if (searchMode is null)
                return Results.BadRequest(new { error = "mode must be hybrid | semantic | lexical" });

            // SPEC-20260923-retrieval-quality RF-003: flat filter params.
            if (!Search.ResolvedSearchFilter.TryResolve(
                    new SearchFilter
                    {
                        SourceType = sourceType,
                        PathPrefix = pathPrefix,
                        IndexedAfter = indexedAfter,
                        Language = language,
                        WindowSize = windowSize,
                        LimitMode = limitMode,
                        AutocutSensitivity = autocutSensitivity
                    }, out var filter, out var error))
                return Results.BadRequest(new { error });

            var k = topK is null or <= 0 ? DefaultTopK : Math.Min(topK.Value, MaxTopK);
            var results = await svc.SearchAsync(query, k, sourceId, searchMode.Value, filter, ct: ct);
            return Results.Ok(new SearchResponse
            {
                Results = results,
                TotalMatches = results.Count,
                LimitModeApplied = filter.EffectiveLimitMode(config)
            });
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
            return Results.Ok(new SearchResponse
            {
                Results = results,
                TotalMatches = results.Count,
                LimitModeApplied = filter.EffectiveLimitMode(config)
            });
        });

        return group;
    }

    internal static SearchMode? ParseMode(string? mode) => mode switch
    {
        null or "" => SearchMode.Semantic,
        _ when Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed) => parsed,
        _ => null
    };
}
