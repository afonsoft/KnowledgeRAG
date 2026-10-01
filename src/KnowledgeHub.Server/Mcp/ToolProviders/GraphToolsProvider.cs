using System.Text;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Read-only knowledge-graph tools (SPEC-20260923-graphrag RF-004):
/// find_dependencies, find_dependents, find_path, analyze_impact.
/// All depth args clamp to 1..3 and results are hard-capped by the effective
/// MaxResults (default 200 edges). Absent entirely when the graph is disabled —
/// flag resolves through <see cref="IGraphSettingsService"/> so /settings
/// edits apply without restart (SPEC-20260923-graph-settings-ui RF-003).
/// </summary>
public sealed class GraphToolsProvider(IGraphSettingsService graphSettings) : IToolProvider
{
    private const int MaxDepth = 3;

    // SPEC-20260924-graph-tool-discovery RF-004: descriptions explain when to
    // use each tool vs search_knowledge, and every example uses entity names
    // that real extraction produces (services, concepts — the same strings the
    // `components` field of search results returns).
    private static readonly JsonObject ComponentSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "component":{"type":"string","description":"Entity name as extracted from indexed text — case/variant-insensitive. Discover real names via search_knowledge: every hit carries a `components` field with the entity names found in that chunk.","examples":["OmniRoute","AgentRouter"]},
          "depth":{"type":"integer","description":"Traversal depth 1-3 (default 1, clamped to 3)"}
        },"required":["component"],
        "examples":[{"component":"OmniRoute","depth":2}]}
        """)!.AsObject();

    private static readonly JsonObject PathSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "a":{"type":"string","description":"Source entity name","examples":["OpenClaw"]},
          "b":{"type":"string","description":"Target entity name","examples":["OmniRoute"]},
          "depth":{"type":"integer","description":"Max path length 1-3 (default 3, clamped)"}
        },"required":["a","b"],
        "examples":[{"a":"OpenClaw","b":"OmniRoute","depth":3}]}
        """)!.AsObject();

    private static readonly JsonObject ImpactSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "component":{"type":"string","description":"Entity name whose dependents are analyzed — discover names via the `components` field of search_knowledge results","examples":["OmniRoute"]}
        },"required":["component"],
        "examples":[{"component":"OmniRoute"}]}
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
                Name = "find_dependencies",
                Title = "Find dependencies",
                Description = "What does this component depend on? Outbound traversal of the knowledge graph built from indexed sources — every edge carries evidence (chunk + document + source). Use when the question is about RELATIONSHIPS ('what does X call/use/need?', 'which services touch Y?'), not text content — for content use search_knowledge first; its results return a `components` field with entity names you can pass here. Unknown names get 'did you mean' suggestions.",
                InputSchema = ComponentSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = (ctx, ct) => TraverseAsync(ctx, ct, GraphDirection.Outbound)
            },
            new CatalogTool
            {
                Name = "find_dependents",
                Title = "Find dependents",
                Description = "What depends on this component? Inbound traversal — answers 'who calls/uses/relies on X?' and 'what breaks if X changes?'. Use for reverse-impact questions on entities extracted from indexed sources; every edge carries evidence (chunk + document + source). Discover entity names via search_knowledge results (`components` field); unknown names get 'did you mean' suggestions.",
                InputSchema = ComponentSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = (ctx, ct) => TraverseAsync(ctx, ct, GraphDirection.Inbound)
            },
            new CatalogTool
            {
                Name = "find_path",
                Title = "Find path",
                Description = "Shortest path between two entities in the knowledge graph (depth-capped BFS, up to 5 paths). Use for connectivity questions — 'how is A related to B?', 'is there a chain from X to Y?' — where both endpoints are known entity names (from search_knowledge `components` or prior graph results). Returns 'no path' when the entities are not connected within the depth cap.",
                InputSchema = PathSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = FindPathAsync
            },
            new CatalogTool
            {
                Name = "analyze_impact",
                Title = "Analyze impact",
                Description = "Impact analysis for a component: 1-hop dependents plus the documents behind each relation's evidence — answers 'if X changes/fails, what is affected and where is it documented?'. Use for change-impact and blast-radius questions. Entity names come from search_knowledge results (`components` field) or graph traversal output; unknown names get 'did you mean' suggestions.",
                InputSchema = ImpactSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = AnalyzeImpactAsync
            }
        ];
        return Task.FromResult(tools);
    }

    private async ValueTask<CallToolResult> TraverseAsync(
        ToolCallContext ctx, CancellationToken ct, GraphDirection direction)
    {
        var component = ToolArgs.RequiredString(ctx, "component");
        var (store, maxEdges) = Resolve(ctx);
        var node = await store.FindNodeAsync(component, ct);
        if (node is null)
            return await UnknownComponent(store, component, ct);

        var depth = ClampDepth(ctx, out var clamped);
        var sub = await store.TraverseAsync(node.Id, direction, depth, maxEdges, ct);
        var payload = FormatSubgraph(sub.Nodes, sub.Edges, node, depth, clamped, sub.Truncated);
        var text = $"{component}: {sub.Edges.Count} edge(s), {sub.Nodes.Count} node(s)" +
            (sub.Truncated ? " (truncated at cap)" : "") +
            (clamped ? $" (depth clamped to {MaxDepth})" : "");
        return await ToolResults.Structured(text, payload);
    }

    private async ValueTask<CallToolResult> FindPathAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var a = ToolArgs.RequiredString(ctx, "a");
        var b = ToolArgs.RequiredString(ctx, "b");
        var (store, _) = Resolve(ctx);
        var from = await store.FindNodeAsync(a, ct);
        if (from is null)
            return await UnknownComponent(store, a, ct);
        var to = await store.FindNodeAsync(b, ct);
        if (to is null)
            return await UnknownComponent(store, b, ct);

        var depth = ClampDepth(ctx, out var clamped);
        var paths = await store.FindPathsAsync(from.Id, to.Id, depth, 5, ct);
        if (paths.Count == 0)
        {
            return await ToolResults.Error(
                $"no path between '{a}' and '{b}' within depth {depth}");
        }

        var nodes = new Dictionary<Guid, KgNode>();
        foreach (var p in paths)
            foreach (var e in p)
            {
                nodes[e.FromNodeId] = e.From;
                nodes[e.ToNodeId] = e.To;
            }
        var payload = new
        {
            paths = paths.Select(p => p.Select(e => new
            {
                from = e.From.Name,
                to = e.To.Name,
                kind = e.Kind,
                evidence = EvidenceOf(e)
            })),
            depthClamped = clamped
        };
        return await ToolResults.Structured(
            $"path {a} → {b}: {paths[0].Count} hop(s)" + (clamped ? $" (depth clamped to {MaxDepth})" : ""),
            payload);
    }

    private async ValueTask<CallToolResult> AnalyzeImpactAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var component = ToolArgs.RequiredString(ctx, "component");
        var (store, maxEdges) = Resolve(ctx);
        var node = await store.FindNodeAsync(component, ct);
        if (node is null)
            return await UnknownComponent(store, component, ct);

        var sub = await store.ImpactAsync(node.Id, maxEdges, ct);
        var docs = sub.Edges
            .Select(e => e.Document)
            .Where(d => d is not null)
            .DistinctBy(d => d.Id)
            .Select(d => new { title = d.Title, uri = d.UriReference });
        var payload = new
        {
            component = node.Name,
            dependents = sub.Edges
                .GroupBy(e => e.Kind)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => new { from = e.From.Name, kind = e.Kind, evidence = EvidenceOf(e) })),
            affectedDocuments = docs,
            truncated = sub.Truncated
        };
        return await ToolResults.Structured(
            $"{component}: {sub.Edges.Count} dependent edge(s), {docs.Count()} affected document(s)",
            payload);
    }

    private (IKnowledgeGraphStore Store, int MaxEdges) Resolve(ToolCallContext ctx) =>
        (ctx.Services.GetRequiredService<IKnowledgeGraphStore>(),
         graphSettings.GetEffective().MaxResults);

    private static int ClampDepth(ToolCallContext ctx, out bool clamped)
    {
        var depth = ToolArgs.OptionalInt(ctx, "depth", 1, 100);
        clamped = depth > MaxDepth || depth < 1;
        return Math.Clamp(depth, 1, MaxDepth);
    }

    private static object EvidenceOf(KgEdge e) => new
    {
        chunkId = e.EvidenceChunkId,
        docTitle = e.Document?.Title,
        uri = e.Document?.UriReference
    };

    private static object FormatSubgraph(
        IReadOnlyList<KgNode> nodes, IReadOnlyList<KgEdge> edges,
        KgNode root, int depth, bool clamped, bool truncated) => new
        {
            component = root.Name,
            depth,
            depthClamped = clamped,
            truncated,
            nodes = nodes.Select(n => new { id = n.Id, name = n.Name, type = n.Type }),
            edges = edges.Select(e => new
            {
                from = e.FromNodeId,
                to = e.ToNodeId,
                kind = e.Kind,
                evidence = EvidenceOf(e)
            })
        };

    private static async ValueTask<CallToolResult> UnknownComponent(
        IKnowledgeGraphStore store, string component, CancellationToken ct)
    {
        var suggestions = await store.SuggestAsync(component, 5, ct);
        var sb = new StringBuilder($"unknown component '{component}'");
        if (suggestions.Count > 0)
            sb.Append(" — did you mean: ").Append(string.Join(", ", suggestions.Select(s => $"'{s.Name}'")));
        return await ToolResults.Error(sb.ToString());
    }
}
