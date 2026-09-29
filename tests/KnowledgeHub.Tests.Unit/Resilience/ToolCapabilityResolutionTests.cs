using KnowledgeHub.Server.Resilience;

namespace KnowledgeHub.Tests.Unit.Resilience;

// SPEC-20260928-resilience-tool-fallback-wiring RF-001/RF-003:
// catalog tool names resolve to providers/capabilities, and candidate
// resolution returns concrete tool names filtered by caller visibility.
public sealed class ToolCapabilityResolutionTests
{
    private static readonly string[] DefaultCatalog =
    [
        "tavily_search", "firecrawl_search", "firecrawl_scrape",
        "ask_question", "query-docs", "search_knowledge", "read_document"
    ];

    [Theory]
    [InlineData("tavily_search", "tavily")]
    [InlineData("firecrawl_scrape", "firecrawl")]
    [InlineData("ask_question", "deepwiki")]
    [InlineData("query-docs", "context7")]
    [InlineData("search_knowledge", "internal_fts")]
    public void ProviderForTool_ResolvesKnownTools(string tool, string provider)
    {
        var reg = new ToolCapabilityRegistry();
        Assert.Equal(provider, reg.ProviderForTool(tool));
    }

    [Fact]
    public void ProviderForTool_Unmapped_ReturnsNull()
    {
        var reg = new ToolCapabilityRegistry();
        Assert.Null(reg.ProviderForTool("read_document"));
    }

    [Fact]
    public void GetCapabilityForTool_Resolves()
    {
        var reg = new ToolCapabilityRegistry();
        Assert.Equal("WebSearch", reg.GetCapabilityForTool("tavily_search"));
        Assert.Equal("DeepDocLookup", reg.GetCapabilityForTool("ask_question"));
    }

    [Fact]
    public void CandidateToolNames_SkipsFailedProvider()
    {
        var reg = new ToolCapabilityRegistry();
        var candidates = reg.CandidateToolNames("tavily_search", DefaultCatalog);
        Assert.Equal(["firecrawl_search"], candidates); // duckduckgo has no tool in catalog
    }

    [Fact]
    public void CandidateToolNames_RespectsAvailableSet()
    {
        var reg = new ToolCapabilityRegistry();
        var candidates = reg.CandidateToolNames("ask_question", DefaultCatalog);
        Assert.Equal(["query-docs", "search_knowledge"], candidates);
    }

    [Fact]
    public void CandidateToolNames_MissingCandidateTool_Skips()
    {
        var reg = new ToolCapabilityRegistry();
        var candidates = reg.CandidateToolNames("tavily_search", ["tavily_search"]);
        Assert.Empty(candidates);
    }

    [Fact]
    public void ConfiguredCapabilities_Extend()
    {
        var reg = new ToolCapabilityRegistry(
            new Dictionary<string, List<string>> { ["Custom"] = ["prov_a", "prov_b"] });
        Assert.Equal("Custom", reg.GetCapabilityForTool("prov_a_thing"));
    }

    [Theory]
    [InlineData("timeout waiting for response", ToolErrorClassifier.ToolErrorClass.Transient)]
    [InlineData("tavily upstream error: 503 service unavailable", ToolErrorClassifier.ToolErrorClass.Transient)]
    [InlineData("429 too many requests", ToolErrorClassifier.ToolErrorClass.Transient)]
    [InlineData("401 unauthorized", ToolErrorClassifier.ToolErrorClass.Permanent)]
    [InlineData("forbidden resource", ToolErrorClassifier.ToolErrorClass.Permanent)]
    [InlineData("something weird happened", ToolErrorClassifier.ToolErrorClass.Unknown)]
    [InlineData(null, ToolErrorClassifier.ToolErrorClass.Unknown)]
    public void ToolErrorClassifier_Classifies(string? text, ToolErrorClassifier.ToolErrorClass expected)
    {
        Assert.Equal(expected, ToolErrorClassifier.Classify(text));
    }
}
