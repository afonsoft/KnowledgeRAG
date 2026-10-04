using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.ToolProviders;
using KnowledgeHub.Tests.Unit.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260927-temporal-episodic-knowledge-graph RF-002..RF-005 tool
// surface: catalog gating, argument validation and result emission.
public sealed class TemporalGraphToolsProviderTests
{
    private static async Task<(SqliteConnection, KnowledgeHubDbContext, TemporalGraphRetriever)> SeedAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var linker = new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance);
        var retriever = new TemporalGraphRetriever(
            db, linker, new UnrestrictedScope(),
            NullLogger<TemporalGraphRetriever>.Instance);
        return (conn, db, retriever);
    }

    private sealed class UnrestrictedScope : KnowledgeHub.Server.Auth.ICallerScopeProvider
    {
        public Task<KnowledgeHub.Server.Auth.CallerScope> GetAsync(CancellationToken ct) =>
            Task.FromResult(KnowledgeHub.Server.Auth.CallerScope.Unrestricted);
    }

    private static async Task<KnowledgeSource> SeedGraphAsync(KnowledgeHubDbContext db)
    {
        var source = new KnowledgeSource
        {
            Name = "src",
            SourceType = KnowledgeHub.Shared.Contracts.SourceType.DocumentFile,
            IsActive = true
        };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "d.md", KnowledgeSourceId = source.Id };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        var a = new KgNode { Name = "Svc-A", NormalizedName = EntityResolver.Normalize("Svc-A"), Type = "service", ObservedAt = DateTime.UtcNow };
        var b = new KgNode { Name = "Db-B", NormalizedName = EntityResolver.Normalize("Db-B"), Type = "database", ObservedAt = DateTime.UtcNow };
        db.KgNodes.AddRange(a, b);
        db.KgEdges.Add(new KgEdge
        {
            FromNodeId = a.Id,
            ToNodeId = b.Id,
            Kind = "USES",
            EvidenceChunkId = Guid.NewGuid(),
            KnowledgeDocumentId = doc.Id,
            KnowledgeSourceId = source.Id,
            ObservedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return source;
    }

    private static ToolCallContext Ctx(IServiceProvider services, string json = "{}") => new()
    {
        Services = services,
        Arguments = JsonDocument.Parse(json).RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
    };

    private static IServiceProvider ServicesWith(TemporalGraphRetriever retriever) =>
        new ServiceCollection().AddSingleton(retriever).BuildServiceProvider();

    private static string TextOf(CallToolResult r) =>
        Assert.IsType<TextContentBlock>(r.Content[0]).Text;

    private static async Task<CatalogTool> ToolAsync(
        TemporalGraphToolsProvider provider, IServiceProvider services, string name) =>
        (await provider.GetToolsAsync(services, default)).Single(t => t.Name == name);

    [Fact]
    public async Task GetTools_Empty_WhenGraphDisabled()
    {
        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Disabled);
        Assert.Empty(await provider.GetToolsAsync(new ServiceCollection().BuildServiceProvider(), default));
    }

    [Fact]
    public async Task GetTools_ListsFiveReadOnlyTools_WhenEnabled()
    {
        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Enabled);
        var tools = await provider.GetToolsAsync(new ServiceCollection().BuildServiceProvider(), default);
        Assert.Equal(
            ["search_graph_temporal", "search_graph_recent", "search_graph_diverse",
             "search_graph_relationships", "search_graph_episode"],
            tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.True(t.ReadOnly));
    }

    [Fact]
    public async Task Temporal_RejectsBadDatesAndInvertedRange()
    {
        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Enabled);
        var services = new ServiceCollection().BuildServiceProvider(); // no retriever needed for validation
        var tool = await ToolAsync(provider, services, "search_graph_temporal");

        var bad = await tool.Handler(Ctx(services, """{"start":"not-a-date"}"""), default);
        Assert.True(bad.IsError);
        Assert.Contains("invalid start", TextOf(bad));

        var badEnd = await tool.Handler(Ctx(services, """{"end":"zzz"}"""), default);
        Assert.True(badEnd.IsError);
        Assert.Contains("invalid end", TextOf(badEnd));

        var inverted = await tool.Handler(Ctx(services, """{"start":"2026-09-27","end":"2026-09-01"}"""), default);
        Assert.True(inverted.IsError);
        Assert.Contains("must precede", TextOf(inverted));
    }

    [Fact]
    public async Task Recent_RejectsUnknownWindow()
    {
        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Enabled);
        var services = new ServiceCollection().BuildServiceProvider();
        var tool = await ToolAsync(provider, services, "search_graph_recent");

        var result = await tool.Handler(Ctx(services, """{"query":"x","window":"3mo"}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("invalid window", TextOf(result));
    }

    [Fact]
    public async Task Diverse_RejectsUnknownLevel()
    {
        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Enabled);
        var services = new ServiceCollection().BuildServiceProvider();
        var tool = await ToolAsync(provider, services, "search_graph_diverse");

        var result = await tool.Handler(Ctx(services, """{"entity":"X","diversityLevel":"extreme"}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("invalid diversityLevel", TextOf(result));
    }

    [Fact]
    public async Task Episode_MalformedOrUnknownId_ReportsError()
    {
        var (conn, db, retriever) = await SeedAsync();
        await using var _ = conn;
        await SeedGraphAsync(db);

        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(retriever);
        var tool = await ToolAsync(provider, services, "search_graph_episode");

        var result = await tool.Handler(Ctx(services, """{"episodeId":"not-a-guid"}"""), default);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Temporal_ReturnsObservedFacts_WhenRetrieverPresent()
    {
        var (conn, db, retriever) = await SeedAsync();
        await using var _ = conn;
        await SeedGraphAsync(db);

        var provider = new TemporalGraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(retriever);
        var tool = await ToolAsync(provider, services, "search_graph_temporal");

        var result = await tool.Handler(Ctx(services, """{"query":"","start":"2020-01-01","end":"2030-01-01"}"""), default);
        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
    }
}
