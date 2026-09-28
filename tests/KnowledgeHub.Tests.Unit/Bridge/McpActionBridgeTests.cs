using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.Bridge;
using KnowledgeHub.Shared.Contracts;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Tests.Unit.Bridge;

/// <summary>
/// SPEC-20260927-mcp-dynamic-rag-action-bridge: marker detection (RF-001),
/// scope-respecting execution with chained-call ceiling (RF-003), and hybrid
/// citation formatting (RF-002).
/// </summary>
public sealed class McpActionBridgeTests
{
    private static SearchResultItem Hit(string text, string? flags = null) => new()
    {
        ChunkText = text,
        DocumentTitle = "doc",
        SourceName = "src",
        SourceId = Guid.NewGuid(),
        Score = 0.9,
        UriReference = "doc.md",
        SuspicionFlags = flags,
        ChunkId = Guid.NewGuid()
    };

    private static CatalogTool Tool(
        string name, string requiredParam = "query",
        Func<ToolCallContext, CancellationToken, ValueTask<CallToolResult>>? handler = null,
        bool readOnly = true) => new()
        {
            Name = name,
            Description = name,
            InputSchema = JsonNode.Parse(
                $"{{\"type\":\"object\",\"properties\":{{\"{requiredParam}\":{{\"type\":\"string\"}}}},\"required\":[\"{requiredParam}\"]}}")!.AsObject(),
            ReadOnly = readOnly,
            Handler = handler ?? ((_, _) => ToolResults.Text("live-data"))
        };

    private static ToolCallContext Ctx() => new() { Services = null! };

    // ---- Detection ---------------------------------------------------------

    [Fact]
    public void Detect_ExplicitMarker_YieldsNameAndArgs()
    {
        var hit = Hit("Para checar o estoque <!-- mcp-tool: sql_query target=\"db_prod\" --> use a tool.");
        var tools = new List<CatalogTool> { Tool("sql_query") };

        var found = ToolActionAnnotationDetector.Detect(null, [hit], tools, 3);

        Assert.Single(found);
        Assert.Equal("sql_query", found[0].ToolName);
        Assert.Equal(ToolActionOrigin.Marker, found[0].Origin);
        Assert.Equal("db_prod", found[0].Args["target"].GetString());
    }

    [Fact]
    public void Detect_MarkerForUnknownOrMetaTool_IsIgnored()
    {
        var hit = Hit("chame <!-- mcp-tool: ghost_tool --> e <!-- mcp-tool: search_knowledge -->");
        var tools = new List<CatalogTool> { Tool("sql_query"), Tool("search_knowledge") };

        var found = ToolActionAnnotationDetector.Detect(null, [hit], tools, 3);
        Assert.Empty(found);
    }

    [Fact]
    public void Detect_FlaggedChunk_MarkersAreIgnored()
    {
        var hit = Hit("<!-- mcp-tool: sql_query -->", flags: "suspicious");
        var tools = new List<CatalogTool> { Tool("sql_query") };

        Assert.Empty(ToolActionAnnotationDetector.Detect(null, [hit], tools, 3));
    }

    [Fact]
    public void Detect_QuestionMentionsTool_YieldsQuestionOrigin()
    {
        var tools = new List<CatalogTool> { Tool("firecrawl_search") };

        var found = ToolActionAnnotationDetector.Detect(
            "use firecrawl_search to fetch the docs", [], tools, 3);

        Assert.Single(found);
        Assert.Equal(ToolActionOrigin.Question, found[0].Origin);
        Assert.Empty(found[0].Args);
    }

    [Fact]
    public void Detect_ToolNotInVisibleCatalog_NeverNominated()
    {
        // Caller-scope guardrail: an invisible (unauthorized) tool can't fire.
        var hit = Hit("<!-- mcp-tool: sql_query -->");
        var found = ToolActionAnnotationDetector.Detect("sql_query", [hit], [], 3);
        Assert.Empty(found);
    }

    [Fact]
    public void Detect_DedupesAndCapsNominations()
    {
        var hits = Enumerable.Range(0, 5)
            .Select(i => Hit($"<!-- mcp-tool: tool_{i} -->"))
            .ToList();
        var tools = Enumerable.Range(0, 5)
            .Select(i => (CatalogTool)Tool($"tool_{i}")).ToList();

        var found = ToolActionAnnotationDetector.Detect(null, hits, tools, 2);
        Assert.Equal(2, found.Count);
    }

    // ---- Execution -----------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ExecutesNominatedTool_RecordsLiveExecution()
    {
        var calls = 0;
        var tool = Tool("sql_query", handler: (ctx, _) =>
        {
            calls++;
            var q = ctx.Arguments!["query"].GetString();
            return ToolResults.Text($"rows for {q}");
        });
        var hit = Hit("<!-- mcp-tool: sql_query query=\"select 1\" -->");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "qual o estoque?", [hit], [tool], Ctx(), 3, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Single(exec);
        Assert.Equal("sql_query", exec[0].ToolName);
        Assert.False(exec[0].IsError);
        Assert.Contains("rows", exec[0].OutputPreview);
        Assert.True(exec[0].TimestampUtc > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task ExecuteAsync_ArglessNomination_FillsQueryFromQuestion()
    {
        string? seen = null;
        var tool = Tool("firecrawl_search", handler: (ctx, _) =>
        {
            seen = ctx.Arguments!["query"].GetString();
            return ToolResults.Text("ok");
        });

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "latest .NET notes", [], [tool], Ctx(), 3, CancellationToken.None);

        // No nomination: question-mention requires the name in the question.
        Assert.Empty(exec);

        exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "use firecrawl_search for latest .NET notes", [], [tool], Ctx(), 3, CancellationToken.None);
        Assert.Single(exec);
        Assert.Equal("use firecrawl_search for latest .NET notes", seen);
    }

    [Fact]
    public async Task ExecuteAsync_CapsChainedCalls()
    {
        var calls = 0;
        ValueTask<CallToolResult> Counting(ToolCallContext ctx, CancellationToken ct)
        {
            calls++;
            return ToolResults.Text("x");
        }
        var hits = Enumerable.Range(0, 5)
            .Select(i => Hit($"<!-- mcp-tool: t{i} -->")).ToList();
        var tools = Enumerable.Range(0, 5)
            .Select(i => (CatalogTool)Tool($"t{i}", handler: Counting)).ToList();

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", hits, tools, Ctx(), maxCalls: 3, CancellationToken.None);

        Assert.Equal(3, calls); // RF-003 hard ceiling
        Assert.Equal(3, exec.Count);
    }

    [Fact]
    public async Task ExecuteAsync_NonReadOnlyTool_NeverExecutes()
    {
        var calls = 0;
        var tool = Tool("mutate", handler: (_, _) =>
        {
            calls++;
            return ToolResults.Text("x");
        }, readOnly: false);
        var hit = Hit("<!-- mcp-tool: mutate -->");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", [hit], [tool], Ctx(), 3, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Empty(exec);
    }

    [Fact]
    public async Task ExecuteAsync_ToolError_IsCapturedNotThrown()
    {
        var tool = Tool("sql_query", handler: (_, _) =>
            ToolResults.Error("connection refused"));
        var hit = Hit("<!-- mcp-tool: sql_query query=\"x\" -->");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", [hit], [tool], Ctx(), 3, CancellationToken.None);

        Assert.Single(exec);
        Assert.True(exec[0].IsError);
    }

    // ---- Citations / context items ------------------------------------------

    [Fact]
    public void FormatLiveCitations_RendersTimestampedLines()
    {
        var exec = new List<LiveToolExecution>
        {
            new()
            {
                ToolName = "sql_query",
                TimestampUtc = new DateTimeOffset(2026, 9, 27, 15, 10, 2, TimeSpan.Zero),
                ArgsSummary = "{\"query\":\"select 1\"}",
                IsError = false,
                OutputPreview = "rows"
            }
        };

        var text = HybridCitationFormatter.FormatLiveCitations(exec);

        Assert.Contains("[Live Tool: sql_query @ 2026-09-27T15:10:02Z", text);
        Assert.Contains("Live MCP Citations", text);
    }

    [Fact]
    public void AsContextItems_LabelsLiveDataAndSkipsErrors()
    {
        var exec = new List<LiveToolExecution>
        {
            new()
            {
                ToolName = "good",
                TimestampUtc = DateTimeOffset.UtcNow,
                ArgsSummary = "{}",
                IsError = false,
                OutputPreview = "42"
            },
            new()
            {
                ToolName = "bad",
                TimestampUtc = DateTimeOffset.UtcNow,
                ArgsSummary = "{}",
                IsError = true,
                OutputPreview = "boom"
            }
        };

        var items = HybridCitationFormatter.AsContextItems(exec);

        Assert.Single(items);
        Assert.Equal("[Live Tool: good]", items[0].DocumentTitle);
        Assert.Contains("42", items[0].ChunkText);
        Assert.Equal("live-mcp", items[0].SourceName);
    }
}
