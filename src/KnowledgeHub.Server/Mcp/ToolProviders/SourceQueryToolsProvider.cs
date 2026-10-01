using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// One <c>query_{source_slug}</c> tool per active source (SPEC-04 RF-001).
/// Rebuilt per request — deactivation removes the tool immediately.
/// </summary>
public sealed class SourceQueryToolsProvider : IToolProvider
{
    private static readonly JsonObject Schema = JsonNode.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Text or question to search within this source","examples":["search term"]},
          "topK":{"type":"integer","description":"Max results (default 5, max 50)"}
        },"required":["query"],
        "examples":[{"query":"search term","topK":5}]}
        """)!.AsObject();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<KnowledgeHubDbContext>();
        var active = await db.Sources.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Description, s.SourceType })
            .ToListAsync(cancellationToken);

        var slugs = ToolSlugger.Assign(active.Select(s => (s.Id, s.Name)));
        return active.Select(source => new CatalogTool
        {
            Name = $"query_{slugs[source.Id]}",
            Title = $"Query {source.Name}",
            Description = $"Semantic search scoped exclusively to the '{source.Name}' source" +
                          (string.IsNullOrWhiteSpace(source.Description) ? "." : $" — {source.Description}"),
            InputSchema = Schema,
            ReadOnly = true,
            IdempotentHint = true,
            SourceId = source.Id,
            Handler = async (ctx, ct) =>
            {
                var query = ToolArgs.RequiredString(ctx, "query");
                var topK = ToolArgs.OptionalInt(ctx, "topK", 5, 50);
                var search = ctx.Services.GetRequiredService<ISearchService>();
                var results = await search.SearchAsync(query, topK, source.Id, ct: ct);
                return await ToolResults.Text(KnowledgeToolsProvider.FormatHits(results));
            }
        }).ToList();
    }
}
