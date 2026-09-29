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

        var found = ToolActionAnnotationDetector.Detect(null, [hit], tools, 3, allowDocumentMarkers: true);

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

        var found = ToolActionAnnotationDetector.Detect(null, [hit], tools, 3, allowDocumentMarkers: true);
        Assert.Empty(found);
    }

    [Fact]
    public void Detect_FlaggedChunk_MarkersAreIgnored()
    {
        var hit = Hit("<!-- mcp-tool: sql_query -->", flags: "suspicious");
        var tools = new List<CatalogTool> { Tool("sql_query") };

        Assert.Empty(ToolActionAnnotationDetector.Detect(null, [hit], tools, 3, allowDocumentMarkers: true));
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
        var found = ToolActionAnnotationDetector.Detect("sql_query", [hit], [], 3, allowDocumentMarkers: true);
        Assert.Empty(found);
    }

    [Fact]
    public void Detect_DedupesAndCapsNominations()
    {
        var hits = Enumerable.Range(0, 5)
            .Select(i => Hit($"<!-- mcp-tool: tool_{i} -->"))
            .ToList();
        var tools = Enumerable.Range(0, 5)
            .Select(i => Tool($"tool_{i}")).ToList();

        var found = ToolActionAnnotationDetector.Detect(null, hits, tools, 2, allowDocumentMarkers: true);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Detect_DocumentMarker_DisabledByDefault_NoNomination()
    {
        // SPEC-20260929-live-actions-bridge-hardening RF-001 (AC-1): markers in
        // indexed chunks are untrusted content — a stored document must not be
        // able to fire tools. Ignored unless the operator opted in.
        var hit = Hit("<!-- mcp-tool: sql_query query=\"select 1\" -->");
        var tools = new List<CatalogTool> { Tool("sql_query") };

        Assert.Empty(ToolActionAnnotationDetector.Detect(null, [hit], tools, 3));
    }

    [Fact]
    public void Detect_DocumentMarkersDisabled_QuestionMentionStillNominates()
    {
        // The trust boundary covers chunk content only — the user's own
        // question remains a valid nomination channel.
        var hit = Hit("<!-- mcp-tool: sql_query query=\"select 1\" -->");
        var tools = new List<CatalogTool> { Tool("sql_query"), Tool("firecrawl_search") };

        var found = ToolActionAnnotationDetector.Detect(
            "use firecrawl_search", [hit], tools, 3);

        Assert.Single(found);
        Assert.Equal("firecrawl_search", found[0].ToolName);
        Assert.Equal(ToolActionOrigin.Question, found[0].Origin);
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
            "qual o estoque?", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: true, CancellationToken.None);

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
            "latest .NET notes", [], [tool], Ctx(), 3,
            allowDocumentMarkers: false, CancellationToken.None);

        // No nomination: question-mention requires the name in the question.
        Assert.Empty(exec);

        exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "use firecrawl_search for latest .NET notes", [], [tool], Ctx(), 3,
            allowDocumentMarkers: false, CancellationToken.None);
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
            .Select(i => Tool($"t{i}", handler: Counting)).ToList();

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", hits, tools, Ctx(), maxCalls: 3,
            allowDocumentMarkers: true, CancellationToken.None);

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
            "", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: true, CancellationToken.None);

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
            "", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: true, CancellationToken.None);

        Assert.Single(exec);
        Assert.True(exec[0].IsError);
    }

    [Fact]
    public async Task ExecuteAsync_DocumentMarker_DisabledByDefault_NeverExecutes()
    {
        // SPEC-20260929 AC-1: a planted marker inside a retrieved chunk must not
        // dispatch a tool call under the default trust boundary.
        var calls = 0;
        var tool = Tool("sql_query", handler: (_, _) =>
        {
            calls++;
            return ToolResults.Text("x");
        });
        var hit = Hit("<!-- mcp-tool: sql_query query=\"select 1\" -->");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: false, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Empty(exec);
    }

    [Fact]
    public async Task ExecuteAsync_FlaggedChunkMarker_NeverExecutes()
    {
        // Even with document markers opted in, SuspicionFlags still wins.
        var calls = 0;
        var tool = Tool("sql_query", handler: (_, _) =>
        {
            calls++;
            return ToolResults.Text("x");
        });
        var hit = Hit("<!-- mcp-tool: sql_query query=\"select 1\" -->", flags: "injection-suspect");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: true, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Empty(exec);
    }

    [Fact]
    public async Task ExecuteAsync_MarkerWithEmptyRequiredArg_IsSkipped()
    {
        // RF-006: a partial marker (query="") must not dispatch a
        // meaningless call — the nomination is rejected, not repaired.
        var calls = 0;
        var tool = Tool("sql_query", handler: (_, _) =>
        {
            calls++;
            return ToolResults.Text("x");
        });
        var hit = Hit("<!-- mcp-tool: sql_query query=\"\" -->");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "what is the stock?", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: true, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Empty(exec);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroChunks_QuestionMention_Executes()
    {
        // RF-005 / AC-4: an explicit user nomination runs even when retrieval
        // returned no chunks — the bridge must not depend on documents.
        var calls = 0;
        var tool = Tool("firecrawl_search", handler: (_, _) =>
        {
            calls++;
            return ToolResults.Text("fresh");
        });

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "use firecrawl_search for the docs", [], [tool], Ctx(), 3,
            allowDocumentMarkers: false, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Single(exec);
        Assert.Equal("firecrawl_search", exec[0].ToolName);
    }

    [Fact]
    public async Task ExecuteAsync_NonTextContent_RendersPlaceholderPreview()
    {
        // RF-006: non-TextContentBlock payloads must surface in the preview as
        // placeholders instead of being silently dropped.
        var tool = Tool("img_tool", handler: (_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                Content =
                [
                    new ImageContentBlock { MimeType = "image/png", Data = new byte[] { 1, 2, 3 } },
                    new TextContentBlock { Text = "caption" }
                ]
            }));
        var hit = Hit("<!-- mcp-tool: img_tool query=\"x\" -->");

        var exec = await McpDynamicRagActionBridge.ExecuteAsync(
            "", [hit], [tool], Ctx(), 3,
            allowDocumentMarkers: true, CancellationToken.None);

        Assert.Single(exec);
        Assert.Contains("[image: image/png]", exec[0].OutputPreview);
        Assert.Contains("caption", exec[0].OutputPreview);
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
