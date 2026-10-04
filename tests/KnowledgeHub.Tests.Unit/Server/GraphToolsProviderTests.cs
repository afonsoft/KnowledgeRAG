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

// Covers SPEC-20260923-graphrag RF-004 tool surface: catalog gating on the
// enabled flag, traversal/path/impact handlers, depth clamp and unknown-name
// suggestions.
public sealed class GraphToolsProviderTests
{
    private static async Task<(SqliteConnection, KnowledgeHubDbContext, SqliteKnowledgeGraphStore)> SeedAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db, new SqliteKnowledgeGraphStore(db, NullLogger<SqliteKnowledgeGraphStore>.Instance));
    }

    private static async Task<(KnowledgeDocument Doc, KgNode A, KgNode B)> SeedDocAndNodes(
        KnowledgeHubDbContext db, SqliteKnowledgeGraphStore store)
    {
        var source = new KnowledgeSource
        {
            Name = "src",
            SourceType = KnowledgeHub.Shared.Contracts.SourceType.DocumentFile,
            IsActive = true
        };
        var doc = new KnowledgeDocument
        {
            Title = "doc",
            UriReference = "doc.md",
            KnowledgeSourceId = source.Id
        };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        var a = await store.ResolveNodeAsync("Svc-A", "service", source.Id, default);
        var b = await store.ResolveNodeAsync("Db-B", "database", source.Id, default);
        return (doc, a, b);
    }

    private static ToolCallContext Ctx(IServiceProvider services, string json = "{}") => new()
    {
        Services = services,
        Arguments = JsonDocument.Parse(json).RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
    };

    private static IServiceProvider ServicesWith(KnowledgeHubDbContext db, IKnowledgeGraphStore store) =>
        new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(store)
            .BuildServiceProvider();

    private static string TextOf(CallToolResult r) =>
        Assert.IsType<TextContentBlock>(r.Content[0]).Text;

    [Fact]
    public async Task GetTools_Empty_WhenGraphDisabled()
    {
        var provider = new GraphToolsProvider(FakeGraphSettings.Disabled);
        var tools = await provider.GetToolsAsync(new ServiceCollection().BuildServiceProvider(), default);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task GetTools_ListsFourReadOnlyTools_WhenEnabled()
    {
        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var tools = await provider.GetToolsAsync(new ServiceCollection().BuildServiceProvider(), default);

        Assert.Equal(
            ["find_dependencies", "find_dependents", "find_path", "analyze_impact"],
            tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.True(t.ReadOnly));
        Assert.All(tools, t => Assert.True(t.IdempotentHint));
    }

    [Fact]
    public async Task FindDependencies_TraversesOutbound()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        var (doc, a, b) = await SeedDocAndNodes(db, store);
        await store.AddEdgesAsync([new KgEdge
        {
            FromNodeId = a.Id, ToNodeId = b.Id, Kind = "USES",
            EvidenceChunkId = Guid.NewGuid(), KnowledgeDocumentId = doc.Id,
            KnowledgeSourceId = doc.KnowledgeSourceId
        }], default);

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "find_dependencies");

        var result = await tool.Handler(Ctx(services, """{"component":"svc-a","depth":1}"""), default);
        Assert.False(result.IsError);
        var text = TextOf(result);
        Assert.Contains("1 edge(s)", text);
        var sc = result.StructuredContent!.Value;
        Assert.Equal("Svc-A", sc.GetProperty("component").GetString());
        Assert.Single(sc.GetProperty("edges").EnumerateArray());
    }

    [Fact]
    public async Task FindDependents_UsesInboundDirection_AndClampsDepth()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        var (doc, a, b) = await SeedDocAndNodes(db, store);
        await store.AddEdgesAsync([new KgEdge
        {
            FromNodeId = a.Id, ToNodeId = b.Id, Kind = "USES",
            EvidenceChunkId = Guid.NewGuid(), KnowledgeDocumentId = doc.Id,
            KnowledgeSourceId = doc.KnowledgeSourceId
        }], default);

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "find_dependents");

        var result = await tool.Handler(Ctx(services, """{"component":"Db-B","depth":99}"""), default);
        Assert.False(result.IsError);
        Assert.Contains("depth clamped to 3", TextOf(result));
        Assert.True(result.StructuredContent!.Value.GetProperty("depthClamped").GetBoolean());
    }

    [Fact]
    public async Task FindPath_ReturnsShortestChain()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        var (doc, a, b) = await SeedDocAndNodes(db, store);
        var c = await store.ResolveNodeAsync("Worker-C", "service", doc.KnowledgeSourceId, default);
        await store.AddEdgesAsync([
            new KgEdge { FromNodeId = a.Id, ToNodeId = b.Id, Kind = "USES",
                EvidenceChunkId = Guid.NewGuid(), KnowledgeDocumentId = doc.Id, KnowledgeSourceId = doc.KnowledgeSourceId },
            new KgEdge { FromNodeId = b.Id, ToNodeId = c.Id, Kind = "USES",
                EvidenceChunkId = Guid.NewGuid(), KnowledgeDocumentId = doc.Id, KnowledgeSourceId = doc.KnowledgeSourceId }],
            default);

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "find_path");

        var result = await tool.Handler(Ctx(services, """{"a":"Svc-A","b":"Worker-C","depth":3}"""), default);
        Assert.False(result.IsError);
        Assert.Contains("hop(s)", TextOf(result));
        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("paths")[0].GetArrayLength());
    }

    [Fact]
    public async Task FindPath_NoPath_ReturnsError()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        await SeedDocAndNodes(db, store); // A and B exist but unconnected

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "find_path");

        var result = await tool.Handler(Ctx(services, """{"a":"Svc-A","b":"Db-B"}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("no path", TextOf(result));
    }

    [Fact]
    public async Task FindPath_UnknownEndpoint_Suggests()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        await SeedDocAndNodes(db, store);

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "find_path");

        var result = await tool.Handler(Ctx(services, """{"a":"Svc-A","b":"Nonexistent"}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("unknown component 'Nonexistent'", TextOf(result));
    }

    [Fact]
    public async Task AnalyzeImpact_GroupsDependentsByKind_WithDocs()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        var (doc, a, b) = await SeedDocAndNodes(db, store);
        await store.AddEdgesAsync([new KgEdge
        {
            FromNodeId = a.Id, ToNodeId = b.Id, Kind = "USES",
            EvidenceChunkId = Guid.NewGuid(), KnowledgeDocumentId = doc.Id,
            KnowledgeSourceId = doc.KnowledgeSourceId
        }], default);

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "analyze_impact");

        var result = await tool.Handler(Ctx(services, """{"component":"Db-B"}"""), default);
        Assert.False(result.IsError);
        Assert.Contains("dependent edge(s)", TextOf(result));
        var sc = result.StructuredContent!.Value;
        Assert.True(sc.GetProperty("affectedDocuments").GetArrayLength() >= 1);
        Assert.True(sc.GetProperty("dependents").TryGetProperty("USES", out var usesKind));
        Assert.True(usesKind.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Traverse_UnknownComponent_SuggestsKnownNames()
    {
        var (conn, db, store) = await SeedAsync();
        await using var _ = conn;
        await SeedDocAndNodes(db, store);

        var provider = new GraphToolsProvider(FakeGraphSettings.Enabled);
        var services = ServicesWith(db, store);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "find_dependencies");

        var result = await tool.Handler(Ctx(services, """{"component":"svc"}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("unknown component", TextOf(result));
    }
}
