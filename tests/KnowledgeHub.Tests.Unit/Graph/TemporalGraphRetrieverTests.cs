using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Graph;

/// <summary>
/// SPEC-20260927-temporal-episodic-knowledge-graph: temporal windows (RF-002),
/// recent sliding windows (RF-003), diversity clustering (RF-004), episode
/// filtering, multi-hop depth, response truncation (RF-005) and soft
/// historicization (RF-001).
/// </summary>
public sealed class TemporalGraphRetrieverTests
{
    private static async Task<(SqliteConnection, KnowledgeHubDbContext, TemporalGraphRetriever, SqliteKnowledgeGraphStore)> SeedAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new SqliteKnowledgeGraphStore(db, NullLogger<SqliteKnowledgeGraphStore>.Instance);
        var linker = new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance);
        var retriever = new TemporalGraphRetriever(
            db, linker, store, new UnrestrictedScope(),
            NullLogger<TemporalGraphRetriever>.Instance);
        return (conn, db, retriever, store);
    }

    private sealed class UnrestrictedScope : KnowledgeHub.Server.Auth.ICallerScopeProvider
    {
        public Task<KnowledgeHub.Server.Auth.CallerScope> GetAsync(CancellationToken ct) =>
            Task.FromResult(KnowledgeHub.Server.Auth.CallerScope.Unrestricted);
    }

    private static KgNode Node(string name, string type = "service", DateTime? observed = null) =>
        new()
        {
            Name = name,
            NormalizedName = EntityResolver.Normalize(name),
            Type = type,
            ObservedAt = observed ?? DateTime.UtcNow
        };

    private static KgEdge Edge(KgNode from, KgNode to, KnowledgeDocument doc, Guid sourceId,
        DateTime? observed = null, Guid? episodeId = null, string kind = "USES") =>
        new()
        {
            FromNodeId = from.Id,
            ToNodeId = to.Id,
            Kind = kind,
            EvidenceChunkId = Guid.NewGuid(),
            KnowledgeDocumentId = doc.Id,
            KnowledgeSourceId = sourceId,
            ObservedAt = observed ?? DateTime.UtcNow,
            EpisodeId = episodeId
        };

    private static KnowledgeDocument Doc(KnowledgeHubDbContext db, out KnowledgeSource source)
    {
        source = new KnowledgeSource
        {
            Name = $"src-{Guid.NewGuid():N}",
            SourceType = KnowledgeHub.Shared.Contracts.SourceType.DocumentFile,
            IsActive = true
        };
        var doc = new KnowledgeDocument
        {
            Title = "doc",
            UriReference = $"doc-{Guid.NewGuid():N}.md",
            KnowledgeSourceId = source.Id
        };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        return doc;
    }

    // ---- CallerScope (SPEC-20260929 RF-006) -------------------------------

    private sealed class ScopedTo(Guid[] allowed) : KnowledgeHub.Server.Auth.ICallerScopeProvider
    {
        public Task<KnowledgeHub.Server.Auth.CallerScope> GetAsync(CancellationToken ct) =>
            Task.FromResult(new KnowledgeHub.Server.Auth.CallerScope(
                null, allowed.ToHashSet(), null));
    }

    private static TemporalGraphRetriever ScopedRetriever(
        KnowledgeHubDbContext db, IKnowledgeGraphStore store, params Guid[] allowed) =>
        new(db, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            store, new ScopedTo(allowed),
            NullLogger<TemporalGraphRetriever>.Instance);

    [Fact]
    public async Task ScopedCaller_NeverSeesForeignSourceFacts()
    {
        // AC-3: caller scoped to sourceA gets nothing from sourceB — edges,
        // node browsing, entity lookup, episode listing all filtered.
        var (conn, db, _, store) = await SeedAsync();
        await using var _ = conn;
        var docA = Doc(db, out var sourceA);
        var docB = Doc(db, out var sourceB);
        var a = Node("alpha");
        var b = Node("beta");
        db.KgNodes.AddRange(a, b);
        db.KgEdges.Add(Edge(a, b, docB, sourceB.Id)); // fact lives in source B
        var episode = new KgEpisode { Kind = "ingestion", KnowledgeSourceId = sourceB.Id };
        db.KgEpisodes.Add(episode);
        await db.SaveChangesAsync();

        var scoped = ScopedRetriever(db, store, sourceA.Id);

        var window = await scoped.SearchTemporalWindowAsync("", null, null, 15);
        Assert.Empty(window.Nodes);
        Assert.Empty(window.Edges);

        await Assert.ThrowsAsync<ArgumentException>(
            () => scoped.SearchEntityRelationshipsAsync("beta"));

        var ep = await scoped.SearchEpisodeContextAsync(episode.Id.ToString());
        Assert.Empty(ep.Nodes);
        Assert.Null(ep.Episode);
    }

    // ---- Temporal window ---------------------------------------------------

    [Fact]
    public async Task TemporalWindow_FiltersByObservedAt()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("service alpha");
        var b = Node("service beta");
        var c = Node("service gamma");
        db.KgNodes.AddRange(a, b, c);
        db.KgEdges.AddRange(
            Edge(a, b, doc, source.Id, DateTime.UtcNow.AddDays(-1)),
            Edge(b, c, doc, source.Id, DateTime.UtcNow.AddDays(-10)));
        await db.SaveChangesAsync();

        var result = await retriever.SearchTemporalWindowAsync(
            "", DateTime.UtcNow.AddDays(-2), DateTime.UtcNow, 15);

        Assert.Single(result.Edges);
        Assert.Equal(b.Id, result.Edges[0].ToNodeId);
        Assert.Equal(2, result.Nodes.Count);
    }

    [Fact]
    public async Task TemporalWindow_NoActivity_ReturnsEmpty()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("service alpha");
        var b = Node("service beta");
        db.KgNodes.AddRange(a, b);
        db.KgEdges.Add(Edge(a, b, doc, source.Id, DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var result = await retriever.SearchTemporalWindowAsync(
            "", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-20), 15);

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task TemporalWindow_StartAfterEnd_Throws()
    {
        var (conn, _, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.SearchTemporalWindowAsync(
                "", DateTime.UtcNow, DateTime.UtcNow.AddDays(-1), 15));
        Assert.Contains("must precede", ex.Message);
    }

    [Fact]
    public async Task TemporalWindow_InvalidatedEdge_RemainsQueryableByObservationTime()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("service alpha");
        var b = Node("service beta");
        db.KgNodes.AddRange(a, b);
        var stale = Edge(a, b, doc, source.Id, DateTime.UtcNow.AddDays(-10));
        stale.ValidTo = DateTime.UtcNow.AddDays(-5);
        db.KgEdges.Add(stale);
        await db.SaveChangesAsync();

        // Temporal search is history-aware: the superseded row is still
        // retrievable in its observation window (ValidTo = metadata).
        var result = await retriever.SearchTemporalWindowAsync(
            "", DateTime.UtcNow.AddDays(-15), DateTime.UtcNow.AddDays(-9), 15);
        Assert.Single(result.Edges);
        Assert.NotNull(result.Edges[0].ValidTo);
    }

    // ---- Recent context ------------------------------------------------------

    [Fact]
    public async Task RecentContext_OnlyFactsInsideWindow()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("service alpha");
        var b = Node("service beta");
        var c = Node("service gamma");
        db.KgNodes.AddRange(a, b, c);
        db.KgEdges.AddRange(
            Edge(a, b, doc, source.Id, DateTime.UtcNow.AddHours(-2)),
            Edge(b, c, doc, source.Id, DateTime.UtcNow.AddHours(-30)));
        await db.SaveChangesAsync();

        var result = await retriever.SearchRecentContextAsync("", TimeSpan.FromHours(24), 10);

        Assert.Single(result.Edges);
        Assert.Equal(DateTime.UtcNow - TimeSpan.FromHours(24), result.WindowStart!.Value, TimeSpan.FromMinutes(1));
    }

    // ---- Entity relationships (multi-hop) -----------------------------------

    [Fact]
    public async Task Relationships_DepthTwo_StopsBeforeThirdHop()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("node alpha");
        var b = Node("node beta");
        var c = Node("node gamma");
        var d = Node("node delta");
        db.KgNodes.AddRange(a, b, c, d);
        db.KgEdges.AddRange(
            Edge(a, b, doc, source.Id),
            Edge(b, c, doc, source.Id),
            Edge(c, d, doc, source.Id));
        await db.SaveChangesAsync();

        var result = await retriever.SearchEntityRelationshipsAsync("node alpha", depth: 2, 15);

        Assert.DoesNotContain(result.Nodes, n => n.Id == d.Id);
        Assert.Contains(result.Nodes, n => n.Id == c.Id);
        Assert.Equal(3, result.Nodes.Count);
    }

    [Fact]
    public async Task Relationships_TraversesBothDirections()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("node alpha");
        var b = Node("node beta");
        db.KgNodes.AddRange(a, b);
        db.KgEdges.Add(Edge(b, a, doc, source.Id)); // inbound to alpha
        await db.SaveChangesAsync();

        var result = await retriever.SearchEntityRelationshipsAsync("node alpha", depth: 1, 15);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Single(result.Edges);
    }

    [Fact]
    public async Task Relationships_UnknownEntity_Throws()
    {
        var (conn, _, retriever, _) = await SeedAsync();
        await using var _ = conn;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.SearchEntityRelationshipsAsync("nonexistent", 2, 15));
    }

    // ---- Diversity -----------------------------------------------------------

    [Fact]
    public async Task Diverse_HighLevel_DistributesAcrossClusters()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var root = Node("root entity");
        root.Labels = ["Core"];
        db.KgNodes.Add(root);

        // 10 nodes of one cluster, 2 of another — AC of the SPEC.
        var dbNodes = Enumerable.Range(0, 10).Select(i => Node($"db node {i}")).ToList();
        foreach (var n in dbNodes) n.Labels = ["Database"];
        var secNodes = Enumerable.Range(0, 2).Select(i => Node($"sec node {i}")).ToList();
        foreach (var n in secNodes) n.Labels = ["Security"];
        db.KgNodes.AddRange(dbNodes.Concat(secNodes));
        db.KgEdges.AddRange(dbNodes.Concat(secNodes)
            .Select(n => Edge(root, n, doc, source.Id)));
        await db.SaveChangesAsync();

        var result = await retriever.SearchDiverseResultsAsync("root entity", "high", 5);

        Assert.True(result.Nodes.Count <= 5);
        Assert.True(result.Nodes.Count(n => n.Labels.Contains("Database")) <= 1,
            "high diversity must cap each cluster at 1 node");
        Assert.Contains(result.Nodes, n => n.Labels.Contains("Security"));
    }

    [Fact]
    public async Task Diverse_InvalidLevel_Throws()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var root = Node("root entity");
        db.KgNodes.Add(root);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.SearchDiverseResultsAsync("root entity", "extreme", 10));
        Assert.Contains("permitted values", ex.Message);
    }

    // ---- Episodes -----------------------------------------------------------

    [Fact]
    public async Task EpisodeContext_ReturnsOnlyEpisodeFacts()
    {
        var (conn, db, retriever, _) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var episode = new KgEpisode
        {
            Kind = "ingestion",
            KnowledgeSourceId = source.Id,
            Summary = "doc (doc.md)"
        };
        db.KgEpisodes.Add(episode);
        var inEp = Node("episode node", observed: DateTime.UtcNow);
        inEp.EpisodeId = episode.Id;
        var outEp = Node("other node");
        db.KgNodes.AddRange(inEp, outEp);
        db.KgEdges.Add(Edge(inEp, outEp, doc, source.Id, episodeId: episode.Id));
        db.KgEdges.Add(Edge(outEp, inEp, doc, source.Id));
        await db.SaveChangesAsync();

        var result = await retriever.SearchEpisodeContextAsync(episode.Id.ToString(), 10);

        Assert.Equal(episode.Id, result.Episode!.Id);
        Assert.Single(result.Nodes);
        Assert.Equal(inEp.Id, result.Nodes[0].Id);
        Assert.Single(result.Edges);
        Assert.Equal(episode.Id, result.Edges[0].EpisodeId);
    }

    [Fact]
    public async Task EpisodeContext_InvalidId_Throws()
    {
        var (conn, _, retriever, _) = await SeedAsync();
        await using var _ = conn;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.SearchEpisodeContextAsync("not-a-guid", 10));
    }

    // ---- Historicization (store level) --------------------------------------

    [Fact]
    public async Task AddEdges_Reobservation_BumpsObservedAt()
    {
        var (conn, db, _, store) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("node alpha");
        var b = Node("node beta");
        db.KgNodes.AddRange(a, b);
        var edge = Edge(a, b, doc, source.Id, DateTime.UtcNow.AddDays(-3));
        db.KgEdges.Add(edge);
        await db.SaveChangesAsync();
        var before = edge.ObservedAt;

        var reobserved = Edge(a, b, doc, source.Id);
        reobserved.EvidenceChunkId = edge.EvidenceChunkId;
        var added = await store.AddEdgesAsync([reobserved], default);

        Assert.Equal(0, added); // re-observation, not a new row
        Assert.True(edge.ObservedAt > before);
    }

    [Fact]
    public async Task AddEdges_NewEvidence_SoftHistoricizesPrior()
    {
        var (conn, db, _, store) = await SeedAsync();
        await using var _ = conn;
        var doc = Doc(db, out var source);
        var a = Node("node alpha");
        var b = Node("node beta");
        db.KgNodes.AddRange(a, b);
        var old = Edge(a, b, doc, source.Id, DateTime.UtcNow.AddDays(-3));
        db.KgEdges.Add(old);
        await db.SaveChangesAsync();

        var added = await store.AddEdgesAsync([Edge(a, b, doc, source.Id)], default);

        Assert.Equal(1, added);
        Assert.NotNull(old.ValidTo);          // superseded, not deleted
        Assert.Equal(2, await db.KgEdges.CountAsync());
        Assert.Equal(1, await db.KgEdges.CountAsync(e => e.ValidTo == null));
    }

    // ---- 8 KB response cap (RF-005) -----------------------------------------

    [Fact]
    public void EnforceSizeLimit_OversizedResult_CondensesUnderCap()
    {
        var observed = DateTime.UtcNow;
        var nodes = Enumerable.Range(0, 60).Select(i => new KgNode
        {
            Name = $"a-very-long-entity-name-for-testing-purposes-{i:D4}",
            NormalizedName = $"a very long entity name for testing purposes {i:D4}",
            Type = "service",
            ObservedAt = observed,
            Labels = ["ClusterA", "Ingestion", "Experimental"]
        }).ToList();
        var edges = Enumerable.Range(0, 60).Select(i => new KgEdge
        {
            FromNodeId = nodes[i].Id,
            ToNodeId = nodes[(i + 1) % nodes.Count].Id,
            Kind = "DEPENDS_ON",
            EvidenceChunkId = Guid.NewGuid(),
            KnowledgeDocumentId = Guid.NewGuid(),
            KnowledgeSourceId = Guid.NewGuid(),
            ObservedAt = observed
        }).ToList();
        var result = new TemporalSearchResult(nodes, edges, null, null, null, false);

        var full = TemporalGraphFormatter.ToPayload(result);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(full, JsonSerializerOptions.Web).Length
            > TemporalGraphRetriever.MaxGraphResponsePreviewBytes,
            "fixture must exceed the 8KB cap to exercise truncation");

        var formatted = TemporalGraphFormatter.EnforceSizeLimit(result);

        Assert.True(formatted.ResponseTruncated);
        Assert.True(
            JsonSerializer.SerializeToUtf8Bytes(formatted.Payload, JsonSerializerOptions.Web).Length
            <= TemporalGraphRetriever.MaxGraphResponsePreviewBytes,
            "condensed payload must respect the 8KB ceiling");
        Assert.Contains("condensed", formatted.Summary);
    }

    [Fact]
    public void EnforceSizeLimit_SmallResult_PassesThrough()
    {
        var n = new KgNode { Name = "tiny", NormalizedName = "tiny", Type = "service" };
        var result = new TemporalSearchResult([n], [], null, null, null, false);

        var formatted = TemporalGraphFormatter.EnforceSizeLimit(result);

        Assert.False(formatted.ResponseTruncated);
    }
}
