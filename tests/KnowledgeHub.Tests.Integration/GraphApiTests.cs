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
/// SPEC-20260928-graph-timeline-viewer RF-001 ACs: /api/graph read surface —
/// auth policy, node list with temporal fields, per-node edges, episodes,
/// and the validity-window intersect semantics of /timeline.
/// </summary>
public class GraphApiTests : IClassFixture<GraphApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"kh-graphapi-{Guid.NewGuid():N}.db"),
                    ["Graph:Enabled"] = "true"
                }));
    }

    private readonly Fixture _factory;
    private readonly HttpClient _admin;

    public GraphApiTests(Fixture factory)
    {
        _factory = factory;
        _admin = TestAuth.Login(factory);
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/graph/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/graph/episodes")).StatusCode);
    }

    [Fact]
    public async Task Nodes_ListReturnsTemporalFields()
    {
        var (nodeId, _, _) = await SeedAsync();
        var res = await _admin.GetFromJsonAsync<JsonElement>("/api/graph/nodes");
        var nodes = res.GetProperty("nodes").EnumerateArray().ToList();
        var node = nodes.FirstOrDefault(n => n.GetProperty("id").GetString() == nodeId.ToString("N"));
        Assert.True(node.ValueKind != JsonValueKind.Undefined);
        Assert.Equal("Svc-A", node.GetProperty("name").GetString());
        Assert.True(node.TryGetProperty("observedAt", out _));
        Assert.True(node.TryGetProperty("validFrom", out _));
        Assert.True(node.TryGetProperty("validTo", out _));
    }

    [Fact]
    public async Task NodeEdges_ResolvesEndpointNames()
    {
        var (nodeId, _, _) = await SeedAsync();
        var edges = await _admin.GetFromJsonAsync<List<JsonElement>>(
            $"/api/graph/nodes/{nodeId}/edges");
        Assert.NotNull(edges);
        var edge = Assert.Single(edges!);
        Assert.Equal("Svc-A", edge.GetProperty("fromName").GetString());
        Assert.Equal("DB-B", edge.GetProperty("toName").GetString());
        Assert.Equal("DEPENDS_ON", edge.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("evidenceChunkId").GetString()));
    }

    [Fact]
    public async Task Timeline_FiltersByValidityWindow()
    {
        var (_, historicalId, _) = await SeedAsync(); // historicized node w/ ValidTo
        var past = DateTime.UtcNow.AddDays(-30);
        var future = DateTime.UtcNow.AddDays(30);

        var inside = await _admin.GetFromJsonAsync<List<JsonElement>>(
            $"/api/graph/timeline?from={past:O}&to={future:O}&includeHistorical=true");
        Assert.Contains(inside!, n => n.GetProperty("id").GetString() == historicalId.ToString("N"));

        // A window that starts *after* the historicized ValidTo must exclude it.
        var afterEnd = DateTime.UtcNow.AddDays(1);
        var outside = await _admin.GetFromJsonAsync<List<JsonElement>>(
            $"/api/graph/timeline?from={afterEnd:O}&includeHistorical=true");
        Assert.DoesNotContain(outside!, n => n.GetProperty("id").GetString() == historicalId.ToString("N"));
    }

    [Fact]
    public async Task Episodes_ReturnsSeededEpisode()
    {
        var (_, _, episodeId) = await SeedAsync(withEpisode: true);
        var rows = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/graph/episodes");
        Assert.Contains(rows!, e => e.GetProperty("id").GetString() == episodeId!.Value.ToString("N"));
    }

    [Fact]
    public async Task Nodes_IncludeHistoricalToggle_HidesHistoricized()
    {
        var (_, historicalId, _) = await SeedAsync();

        var current = await _admin.GetFromJsonAsync<JsonElement>("/api/graph/nodes");
        Assert.DoesNotContain(current.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("id").GetString() == historicalId.ToString("N"));

        var all = await _admin.GetFromJsonAsync<JsonElement>(
            "/api/graph/nodes?includeHistorical=true");
        Assert.Contains(all.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("id").GetString() == historicalId.ToString("N"));
    }

    /// <summary>Seeds source → doc → chunk → 2 nodes + 1 edge + 1 historicized
    /// node (+ optional episode). Returns (vigenteNodeId, historicalNodeId, episodeId?).</summary>
    private async Task<(Guid NodeId, Guid HistoricalId, Guid? EpisodeId)> SeedAsync(bool withEpisode = false)
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
            Title = "graph.md",
            UriReference = "docs/graph.md",
            KnowledgeSourceId = source.Id
        };
        var chunk = new DocumentChunk
        {
            KnowledgeDocumentId = doc.Id,
            ChunkIndex = 0,
            TextContent = "Svc-A depends on DB-B."
        };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);

        Guid? episodeId = null;
        if (withEpisode)
        {
            var ep = new KgEpisode { Kind = "ingestion", KnowledgeSourceId = source.Id, Summary = "seed" };
            db.KgEpisodes.Add(ep);
            episodeId = ep.Id;
        }
        await db.SaveChangesAsync();

        var a = await store.ResolveNodeAsync("Svc-A", "service", source.Id, default, episodeId);
        var b = await store.ResolveNodeAsync("DB-B", "database", source.Id, default);
        var old = await store.ResolveNodeAsync("Legacy-C", "service", source.Id, default);
        await store.AddEdgesAsync([new KgEdge
        {
            FromNodeId = a.Id, ToNodeId = b.Id, Kind = "DEPENDS_ON",
            EvidenceChunkId = chunk.Id, KnowledgeDocumentId = doc.Id,
            KnowledgeSourceId = source.Id, PromptVersion = "v1"
        }], default);

        // Historicize: supersede Legacy-C (ValidTo in the past).
        old.ValidTo = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        return (a.Id, old.Id, episodeId);
    }
}
