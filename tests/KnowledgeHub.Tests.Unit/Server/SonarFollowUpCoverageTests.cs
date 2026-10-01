using System.Text.Json;
using KnowledgeHub.Server.A2A;
using KnowledgeHub.Server.Configuration;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Chunking;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Mcp.Upstream;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>Coverage for paths exercised by the sonarqube-autofix pass
/// (SPEC-20261001-sonarqube-backlog-cleanup) — config parsing, glob compile,
/// filter resolution, agent card, chunk dispatch.</summary>
public sealed class SonarFollowUpCoverageTests
{
    // ---------------- GlobMatcher (DocumentFileConnector) ----------------

    [Theory]
    [InlineData("*.md", "a.md", true)]
    [InlineData("*.md", "a.txt", false)]
    [InlineData("*.md", "sub/a.md", false)]
    [InlineData("**/*.md", "sub/a.md", true)]
    [InlineData("**/*.md", "a.md", true)]
    [InlineData("**/*", "sub/dir/a.txt", true)]
    [InlineData("a?.txt", "ab.txt", true)]
    [InlineData("a?.txt", "a.txt", false)]
    [InlineData("{md,txt}", "md", true)]
    [InlineData("{md,txt}", "json", false)]
    [InlineData("a.{md,txt}", "a.md", true)]
    [InlineData("a.{md,txt}", "a.txt", true)]
    [InlineData("a.{md,txt}", "a.json", false)]
    [InlineData("notes/**", "notes/x/y.md", true)]
    public void GlobMatcher_Compile_MatchesExpected(string glob, string path, bool expected)
        => Assert.Equal(expected, DocumentFileConnector.GlobMatcher.Compile(glob)(path));

    [Fact]
    public void GlobMatcher_Compile_NormalizesBackslashPaths()
        => Assert.True(DocumentFileConnector.GlobMatcher.Compile("**/*.md")(@"sub\dir\a.md"));

    // ---------------- McpProxyToolsProvider.ParseConfig ----------------

    private static McpProxyToolsProvider NewProvider() => new(
        new McpProxySourceServiceTests.FakeSecretStore(),
        NullLoggerFactory.Instance,
        NullLogger<McpProxyToolsProvider>.Instance);

    private static KnowledgeSource Source(string json) => new()
    {
        Name = "test-src",
        SourceType = SourceType.McpProxy,
        ConfigurationJson = json
    };

    [Fact]
    public void ParseConfig_ValidHttp_ReturnsConfig()
    {
        var cfg = NewProvider().ParseConfig(
            Source("""{"endpoint":"https://mcp.example.com/sse","transport":"sse","namePrefix":"x_","toolsCacheSeconds":30}"""));
        Assert.NotNull(cfg);
        Assert.Equal(HttpTransportMode.Sse, cfg!.Transport);
        Assert.Equal("x_", cfg.NamePrefix);
        Assert.Equal(30, cfg.ToolsCacheSeconds);
    }

    [Fact]
    public void ParseConfig_Defaults_WhenOptionalFieldsMissing()
    {
        var cfg = NewProvider().ParseConfig(Source("""{"endpoint":"https://mcp.example.com/mcp"}"""));
        Assert.NotNull(cfg);
        Assert.Equal(HttpTransportMode.AutoDetect, cfg!.Transport);
        Assert.Equal(300, cfg.ToolsCacheSeconds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("""{"endpoint":"notaurl"}""")]
    [InlineData("""{"endpoint":"ftp://x"}""")]
    [InlineData("""{"transport":"bogus","endpoint":"https://mcp.example.com"}""")]
    public void ParseConfig_Invalid_ReturnsNull(string json)
        => Assert.Null(NewProvider().ParseConfig(Source(json)));

    // ---------------- ResolvedSearchFilter.TryResolve ----------------

    [Fact]
    public void TryResolve_NullFilter_ReturnsEmpty()
    {
        Assert.True(ResolvedSearchFilter.TryResolve(null, out var resolved, out var error));
        Assert.Null(error);
        Assert.Null(resolved.SourceType);
    }

    [Theory]
    [InlineData("bogus-type")]
    [InlineData("not-a-source")]
    public void TryResolve_InvalidSourceType_Fails(string st)
    {
        Assert.False(ResolvedSearchFilter.TryResolve(new SearchFilter { SourceType = st }, out _, out var error));
        Assert.Contains("invalid sourceType", error);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("99/99/9999")]
    public void TryResolve_InvalidIndexedAfter_Fails(string ia)
    {
        Assert.False(ResolvedSearchFilter.TryResolve(new SearchFilter { IndexedAfter = ia }, out _, out var error));
        Assert.Contains("invalid indexedAfter", error);
    }

    [Fact]
    public void TryResolve_ValidIsoDate_Parses()
    {
        Assert.True(ResolvedSearchFilter.TryResolve(
            new SearchFilter { IndexedAfter = "2026-01-15T10:30:00Z" }, out var resolved, out _));
        Assert.NotNull(resolved.IndexedAfter);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T10:30:00Z"), resolved.IndexedAfter);
    }

    [Theory]
    [InlineData("off"), InlineData("multi"), InlineData("hyde"), InlineData("both")]
    public void TryResolve_ValidExpand_Succeeds(string expand)
        => Assert.True(ResolvedSearchFilter.TryResolve(new SearchFilter { Expand = expand }, out _, out _));

    [Fact]
    public void TryResolve_InvalidExpand_Fails()
        => Assert.False(ResolvedSearchFilter.TryResolve(new SearchFilter { Expand = "nope" }, out _, out _));

    // ---------------- A2AEndpointExtensions.BuildAgentCard ----------------

    [Fact]
    public void BuildAgentCard_PushEnabled_ExposesCapability()
    {
        var card = A2AEndpointExtensions.BuildAgentCard(new Uri("https://kh.example.com/"), pushEnabled: true);
        Assert.True(card.Capabilities.PushNotifications);
        Assert.Equal(2, card.SupportedInterfaces.Count);
        Assert.Contains(card.Skills, s => s.InputModes is { Count: > 0 });
    }

    [Fact]
    public void BuildAgentCard_PushDisabled_HidesCapability()
    {
        var card = A2AEndpointExtensions.BuildAgentCard(new Uri("https://kh.example.com/"), pushEnabled: false);
        Assert.False(card.Capabilities.PushNotifications);
    }

    // ---------------- ChunkerSelector ----------------

    [Fact]
    public async Task ChunkAsync_MarkdownPath_ReturnsPieces()
    {
        var text = "# Title\n\n" + string.Join(' ', Enumerable.Repeat("word", 400)) + "\n\n## Second\n\nMore text.";
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingestion:Chunking:Strategy"] = "recursive" })
            .Build();
        var (kind, pieces) = await ChunkerSelector.ChunkAsync(
            "doc.md", text, maxTokens: 100, overlapTokens: 10,
            new KnowledgeHub.Server.Embeddings.DeterministicEmbeddingProvider(), cfg, strategy: null,
            NullLogger.Instance, CancellationToken.None);
        Assert.Equal(ChunkKind.Markdown, kind);
        Assert.NotEmpty(pieces);
        Assert.All(pieces, p => Assert.False(string.IsNullOrWhiteSpace(p.Text)));
    }
}
