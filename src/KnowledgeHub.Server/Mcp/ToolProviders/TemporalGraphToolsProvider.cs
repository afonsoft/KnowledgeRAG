using System.Text.Json.Nodes;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Temporal and episodic knowledge-graph tools
/// (SPEC-20260927-temporal-episodic-knowledge-graph RF-002..RF-005):
/// search_graph_temporal, search_graph_recent, search_graph_diverse,
/// search_graph_relationships, search_graph_episode. Responses are capped at
/// 8 KB of JSON — oversized subgraphs are condensed with a truncation warning.
/// Absent entirely when the graph is disabled.
/// </summary>
public sealed class TemporalGraphToolsProvider(IGraphSettingsService graphSettings) : IToolProvider
{
    private const string MaxResultsParam = "maxResults";
    private static readonly string WindowList =
        string.Join(", ", TemporalDateParser.SupportedWindows);

    private static readonly JsonObject TemporalSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Free-text query — matched against entity names; empty browses the whole window","examples":["connector architecture"]},
          "start":{"type":"string","description":"Window start — RFC3339/ISO-8601, yyyy-MM-ddTHH:mm:ss, yyyy-MM-dd HH:mm:ss or yyyy-MM-dd (UTC assumed)","examples":["2026-09-01","2026-09-20T10:00:00Z"]},
          "end":{"type":"string","description":"Window end — same formats as start; must be after start","examples":["2026-09-27"]},
          "maxResults":{"type":"integer","description":"Max nodes returned (default 15, cap 100)"}
        },"required":[],
        "examples":[{"query":"arquitetura de conectores","start":"2026-09-01","end":"2026-09-27"}]}
        """)!.AsObject();

    private static readonly JsonObject RecentSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Free-text query — matched against entity names; empty browses the whole window","examples":["arquitetura de conectores"]},
          "window":{"type":"string","description":"Sliding window — one of: 1h, 6h, 24h, 7d","enum":["1h","6h","24h","7d"]},
          "maxResults":{"type":"integer","description":"Max nodes returned (default 10, cap 100)"}
        },"required":["query","window"],
        "examples":[{"query":"arquitetura de conectores","window":"24h","maxResults":10}]}
        """)!.AsObject();

    private static readonly JsonObject DiverseSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "entity":{"type":"string","description":"Entity name to diversify around — discover names via the `components` field of search_knowledge results","examples":["OmniRoute"]},
          "diversityLevel":{"type":"string","description":"Cluster spread: low = up to 5 nodes/cluster, medium = 2, high = 1","enum":["low","medium","high"]},
          "maxResults":{"type":"integer","description":"Max nodes returned (default 10, cap 100)"}
        },"required":["entity"],
        "examples":[{"entity":"OmniRoute","diversityLevel":"high","maxResults":10}]}
        """)!.AsObject();

    private static readonly JsonObject RelationshipsSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "entity":{"type":"string","description":"Entity name to explore — discover names via the `components` field of search_knowledge results","examples":["OmniRoute"]},
          "depth":{"type":"integer","description":"Multi-hop depth 1-3 (default 2, clamped to 3)"},
          "maxResults":{"type":"integer","description":"Max nodes returned (default 15, cap 100)"}
        },"required":["entity"],
        "examples":[{"entity":"OmniRoute","depth":2}]}
        """)!.AsObject();

    private static readonly JsonObject EpisodeSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "episodeId":{"type":"string","description":"Episode GUID — surfaced as `episodeId` on temporal/diverse search results","examples":["3fa85f64-5717-4562-b3fc-2c963f66afa6"]},
          "maxResults":{"type":"integer","description":"Max nodes returned (default 10, cap 100)"}
        },"required":["episodeId"],
        "examples":[{"episodeId":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!graphSettings.GetEffective().Enabled)
            return Task.FromResult<IReadOnlyList<CatalogTool>>([]);

        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "search_graph",
                Title = "Search temporal and episodic graph",
                Description = "Search graph with mode: 'temporal', 'recent', 'diverse', 'relationships', or 'episode'.",
                InputSchema = JsonNode.Parse("""
                    {"type":"object","properties":{
                      "mode":{"type":"string","enum":["temporal","recent","diverse","relationships","episode"],"description":"Search mode"},
                      "query":{"type":"string","description":"Free-text query"},
                      "start":{"type":"string","description":"Window start"},
                      "end":{"type":"string","description":"Window end"},
                      "window":{"type":"string","description":"Sliding window (1h, 6h, 24h, 7d)"},
                      "entity":{"type":"string","description":"Entity name"},
                      "diversityLevel":{"type":"string","description":"Diversity level (low, medium, high)"},
                      "depth":{"type":"integer","description":"Depth 1-3"},
                      "episodeId":{"type":"string","description":"Episode GUID"},
                      "maxResults":{"type":"integer","description":"Max results"}
                    },"required":["mode"]}
                    """)!.AsObject(),
                ReadOnly = true,
                IdempotentHint = true,
                Handler = async (ctx, ct) =>
                {
                    var mode = ToolArgs.String(ctx, "mode") ?? "recent";
                    return mode switch
                    {
                        "temporal" => await TemporalAsync(ctx, ct),
                        "recent" => await RecentAsync(ctx, ct),
                        "diverse" => await DiverseAsync(ctx, ct),
                        "relationships" => await RelationshipsAsync(ctx, ct),
                        "episode" => await EpisodeAsync(ctx, ct),
                        _ => await ToolResults.Error($"unknown mode '{mode}'")
                    };
                }
            },
            new CatalogTool
            {
                Name = "search_graph_temporal",
                Title = "[DEPRECATED] Search graph by time window",
                Description = "[DEPRECATED: use search_graph with mode='temporal']",
                InputSchema = TemporalSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = TemporalAsync
            },
            new CatalogTool
            {
                Name = "search_graph_recent",
                Title = "[DEPRECATED] Search graph recent facts",
                Description = "[DEPRECATED: use search_graph with mode='recent']",
                InputSchema = RecentSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = RecentAsync
            },
            new CatalogTool
            {
                Name = "search_graph_diverse",
                Title = "[DEPRECATED] Diversified graph search",
                Description = "[DEPRECATED: use search_graph with mode='diverse']",
                InputSchema = DiverseSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = DiverseAsync
            },
            new CatalogTool
            {
                Name = "search_graph_relationships",
                Title = "[DEPRECATED] Multi-hop graph relationships",
                Description = "[DEPRECATED: use search_graph with mode='relationships']",
                InputSchema = RelationshipsSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = RelationshipsAsync
            },
            new CatalogTool
            {
                Name = "search_graph_episode",
                Title = "[DEPRECATED] Search graph by episode",
                Description = "[DEPRECATED: use search_graph with mode='episode']",
                InputSchema = EpisodeSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = EpisodeAsync
            }
        ];
        return Task.FromResult(tools);
    }

    private static async ValueTask<CallToolResult> TemporalAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var query = ToolArgs.OptionalString(ctx, "query") ?? "";
        var startArg = ToolArgs.OptionalString(ctx, "start");
        var endArg = ToolArgs.OptionalString(ctx, "end");
        var maxResults = ToolArgs.OptionalInt(ctx, MaxResultsParam, 15, 100);

        DateTime? start = null, end = null;
        if (startArg is not null)
        {
            if (!TemporalDateParser.TryParse(startArg, out var parsed))
                return await ToolResults.Error(InvalidDate("start", startArg));
            start = parsed;
        }
        if (endArg is not null)
        {
            if (!TemporalDateParser.TryParse(endArg, out var parsed))
                return await ToolResults.Error(InvalidDate("end", endArg));
            end = parsed;
        }
        if (start is not null && end is not null && start > end)
            return await ToolResults.Error(
                $"invalid range: start '{startArg}' must precede end '{endArg}'");

        var result = await Retriever(ctx).SearchTemporalWindowAsync(query, start, end, maxResults, ct);
        return await Emit(result);
    }

    private static async ValueTask<CallToolResult> RecentAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var query = ToolArgs.RequiredString(ctx, "query");
        var windowArg = ToolArgs.RequiredString(ctx, "window");
        var maxResults = ToolArgs.OptionalInt(ctx, MaxResultsParam, 10, 100);

        if (!TemporalDateParser.TryParseWindow(windowArg, out var window))
            return await ToolResults.Error(
                $"invalid window '{windowArg}' — permitted values: {WindowList}");

        var result = await Retriever(ctx).SearchRecentContextAsync(query, window, maxResults, ct);
        return await Emit(result);
    }

    private static async ValueTask<CallToolResult> DiverseAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var entity = ToolArgs.RequiredString(ctx, "entity");
        var level = ToolArgs.OptionalString(ctx, "diversityLevel") ?? "medium";
        var maxResults = ToolArgs.OptionalInt(ctx, MaxResultsParam, 10, 100);

        if (!DiversityRanker.IsValidLevel(level))
            return await ToolResults.Error(
                $"invalid diversityLevel '{level}' — permitted values: " +
                string.Join(", ", DiversityRanker.Levels));

        TemporalSearchResult result;
        try
        {
            result = await Retriever(ctx).SearchDiverseResultsAsync(entity, level, maxResults, ct);
        }
        catch (ArgumentException)
        {
            return await ToolResults.Error($"unknown entity '{entity}'");
        }
        return await Emit(result);
    }

    private static async ValueTask<CallToolResult> RelationshipsAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var entity = ToolArgs.RequiredString(ctx, "entity");
        var depth = ToolArgs.OptionalInt(ctx, "depth", TemporalGraphRetriever.DefaultDepth, 100);
        var maxResults = ToolArgs.OptionalInt(ctx, MaxResultsParam, 15, 100);

        TemporalSearchResult result;
        try
        {
            result = await Retriever(ctx).SearchEntityRelationshipsAsync(entity, depth, maxResults, ct);
        }
        catch (ArgumentException)
        {
            return await ToolResults.Error($"unknown entity '{entity}'");
        }
        return await Emit(result);
    }

    private static async ValueTask<CallToolResult> EpisodeAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var episodeArg = ToolArgs.RequiredString(ctx, "episodeId");
        var maxResults = ToolArgs.OptionalInt(ctx, MaxResultsParam, 10, 100);

        TemporalSearchResult result;
        try
        {
            result = await Retriever(ctx).SearchEpisodeContextAsync(episodeArg, maxResults, ct);
        }
        catch (ArgumentException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        if (result.Episode is null)
            return await ToolResults.Error($"unknown episode '{episodeArg}'");
        return await Emit(result);
    }

    private static TemporalGraphRetriever Retriever(ToolCallContext ctx) =>
        ctx.Services.GetRequiredService<TemporalGraphRetriever>();

    private static string InvalidDate(string name, string value) =>
        $"invalid {name} '{value}' — expected RFC3339/ISO-8601, " +
        "yyyy-MM-ddTHH:mm:ss, yyyy-MM-dd HH:mm:ss or yyyy-MM-dd (UTC assumed)";

    private static ValueTask<CallToolResult> Emit(TemporalSearchResult result)
    {
        var formatted = TemporalGraphFormatter.EnforceSizeLimit(result);
        return ToolResults.Structured(formatted.Summary, formatted.Payload);
    }
}
