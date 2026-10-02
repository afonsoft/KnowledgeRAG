using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-graphrag ACs: graph tools appear only when enabled, depth is
/// clamped, unknown components fail soft with suggestions, and every returned
/// edge traces back to evidence chunk → document → source (provenance E2E).
/// </summary>
public class GraphToolsTests : IClassFixture<GraphToolsTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-graph-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["Graph:Enabled"] = "true"
                }));
        }
    }

    private readonly Fixture _factory;
    private readonly HttpClient _admin;

    public GraphToolsTests(Fixture factory)
    {
        _factory = factory;
        _admin = TestAuth.Login(factory);
    }

    /// <summary>Seeds source → document → chunk → nodes/edges with provenance.</summary>
    private async Task<(Guid ChunkId, string DocTitle)> SeedGraphAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var store = new SqliteKnowledgeGraphStore(db, NullLogger<SqliteKnowledgeGraphStore>.Instance);

        var source = new KnowledgeSource
        {
            Name = $"graph-{Guid.NewGuid():N}",
            SourceType = SourceType.DocumentFile,
            IsActive = true,
            ConfigurationJson = """{"graph":true}"""
        };
        var doc = new KnowledgeDocument
        {
            Title = "architecture.md",
            UriReference = "docs/architecture.md",
            KnowledgeSourceId = source.Id
        };
        var chunk = new DocumentChunk
        {
            KnowledgeDocumentId = doc.Id,
            ChunkIndex = 0,
            TextContent = "API-X depends on DB-Y. Service-Z uses API-X."
        };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var api = await store.ResolveNodeAsync("API-X", "api", source.Id, default);
        var dbY = await store.ResolveNodeAsync("DB-Y", "database", source.Id, default);
        var svc = await store.ResolveNodeAsync("Service-Z", "service", source.Id, default);
        await store.AddEdgesAsync([
            new KgEdge
            {
                FromNodeId = api.Id, ToNodeId = dbY.Id, Kind = "DEPENDS_ON",
                EvidenceChunkId = chunk.Id, KnowledgeDocumentId = doc.Id,
                KnowledgeSourceId = source.Id, PromptVersion = "v1"
            },
            new KgEdge
            {
                FromNodeId = svc.Id, ToNodeId = api.Id, Kind = "USES",
                EvidenceChunkId = chunk.Id, KnowledgeDocumentId = doc.Id,
                KnowledgeSourceId = source.Id, PromptVersion = "v1"
            }], default);
        return (chunk.Id, doc.Title);
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object args)
    {
        var response = await client.PostAsJsonAsync($"/api/tools/{name}", args);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json;
    }

    [Fact]
    public async Task Catalog_ListsGraphTools_WhenEnabled()
    {
        var response = await _admin.GetFromJsonAsync<JsonElement>("/api/tools/");
        var names = response.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("find_dependencies", names);
        Assert.Contains("find_dependents", names);
        Assert.Contains("find_path", names);
        Assert.Contains("analyze_impact", names);
    }

    [Fact]
    public async Task FindDependencies_Depth2_ReturnsBothEdges_WithProvenance()
    {
        var (chunkId, docTitle) = await SeedGraphAsync();

        var json = await CallToolAsync(_admin, "find_dependencies",
            new { component = "Service-Z", depth = 2 });

        Assert.False(json.GetProperty("isError").GetBoolean());
        var sc = json.GetProperty("structuredContent");
        // The fixture DB is shared — other tests seed more edges; assert on the
        // provenance chain of this test's chunk.
        var edges = sc.GetProperty("edges").EnumerateArray()
            .Where(e => e.GetProperty("evidence").GetProperty("chunkId").GetGuid() == chunkId)
            .ToList();
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e =>
        {
            var evidence = e.GetProperty("evidence");
            Assert.Equal(chunkId, evidence.GetProperty("chunkId").GetGuid());
            Assert.Equal(docTitle, evidence.GetProperty("docTitle").GetString());
            Assert.Equal("docs/architecture.md", evidence.GetProperty("uri").GetString());
        });
    }

    [Fact]
    public async Task FindDependents_InboundTraversal()
    {
        var (chunkId, _) = await SeedGraphAsync();
        var json = await CallToolAsync(_admin, "find_dependents",
            new { component = "db y", depth = 2 }); // normalized lookup

        Assert.False(json.GetProperty("isError").GetBoolean());
        var edges = json.GetProperty("structuredContent")
            .GetProperty("edges").EnumerateArray()
            .Where(e => e.GetProperty("evidence").GetProperty("chunkId").GetGuid() == chunkId)
            .ToList();
        Assert.Equal(2, edges.Count); // API-X→DB-Y and Service-Z→API-X
    }

    [Fact]
    public async Task Depth9_IsClampedTo3()
    {
        await SeedGraphAsync();
        var json = await CallToolAsync(_admin, "find_dependencies",
            new { component = "Service-Z", depth = 9 });

        var sc = json.GetProperty("structuredContent");
        Assert.True(sc.GetProperty("depthClamped").GetBoolean());
        Assert.Equal(3, sc.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task UnknownComponent_IsError_WithSuggestions()
    {
        await SeedGraphAsync();
        var json = await CallToolAsync(_admin, "find_dependencies",
            new { component = "Service" });

        Assert.True(json.GetProperty("isError").GetBoolean());
        var text = json.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("unknown component", text);
        Assert.Contains("Service-Z", text); // suggestion surfaced
    }

    [Fact]
    public async Task FindPath_ReturnsShortestPath()
    {
        await SeedGraphAsync();
        var json = await CallToolAsync(_admin, "find_path",
            new { a = "Service-Z", b = "DB-Y", depth = 3 });

        Assert.False(json.GetProperty("isError").GetBoolean());
        var path = json.GetProperty("structuredContent")
            .GetProperty("paths")[0].EnumerateArray().ToList();
        Assert.Equal(2, path.Count); // Service-Z→API-X→DB-Y
    }

    [Fact]
    public async Task AnalyzeImpact_ReturnsDependentsGroupedByKind()
    {
        await SeedGraphAsync();
        var json = await CallToolAsync(_admin, "analyze_impact",
            new { component = "API-X" });

        Assert.False(json.GetProperty("isError").GetBoolean());
        var sc = json.GetProperty("structuredContent");
        Assert.True(sc.GetProperty("dependents").TryGetProperty("USES", out _));
        var titles = sc.GetProperty("affectedDocuments").EnumerateArray()
            .Select(d => d.GetProperty("title").GetString()).Distinct().ToList();
        Assert.Equal(["architecture.md"], titles);
    }
}
