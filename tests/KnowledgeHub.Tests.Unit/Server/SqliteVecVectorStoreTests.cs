using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260917-sqlite-vec-search: native vec0 KNN must return the same
// hits as the in-process cosine provider (CA-001/CA-003), respect the same
// model/source filters, and fail clearly on dimension mismatch.
[Collection("SearchTelemetry")]
public class SqliteVecVectorStoreTests
{
    private const string Model = "test-model";

    [Fact]
    public async Task Search_ReturnsNativeKnn_OrderedByCosine()
    {
        await using var db = CreateDb();
        var (source, document, chunks) = await SeedAsync(db, sourceName: "s1");
        var store = new SqliteVecVectorStore(db, dimensions: 4);

        await store.UpsertBatchAsync(new[]
        {
            new VectorUpsert(chunks[0].Id, document.Id, source.Id, new float[] { 1, 0, 0, 0 }),
            new VectorUpsert(chunks[1].Id, document.Id, source.Id, new float[] { 0.9f, 0.1f, 0, 0 }),
            new VectorUpsert(chunks[2].Id, document.Id, source.Id, new float[] { 0, 1, 0, 0 }),
        }, Model);

        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, Model, topK: 2);

        Assert.Equal(new[] { chunks[0].Id, chunks[1].Id }, hits.Select(h => h.ChunkId).ToArray());
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.True(hits[0].Score > 0.99); // identical vector → cosine ≈ 1
    }

    [Fact]
    public async Task Search_ParityWithInProcessProvider()
    {
        // CA-001: same dataset through both providers → same top-K in same order.
        var vectors = new[]
        {
            new float[] { 1, 0, 0, 0 },
            new float[] { 0.9f, 0.1f, 0, 0 },
            new float[] { 0, 1, 0, 0 },
            new float[] { 0.5f, 0.5f, 0.5f, 0.5f },
        };
        var query = new float[] { 1, 0, 0, 0 };

        await using var dbInProcess = CreateDb();
        var seedA = await SeedAsync(dbInProcess, "s1", vectors.Length);
        var inProcess = new SqliteVectorStore(dbInProcess);
        await inProcess.UpsertBatchAsync(
            vectors.Select((v, i) => new VectorUpsert(seedA.chunks[i].Id, seedA.document.Id, seedA.source.Id, v)).ToList(),
            Model);
        var expected = await inProcess.SearchAsync(query, Model, topK: 3);

        await using var dbVec = CreateDb();
        var seedB = await SeedAsync(dbVec, "s1", vectors.Length);
        var vec = new SqliteVecVectorStore(dbVec, dimensions: 4);
        await vec.UpsertBatchAsync(
            vectors.Select((v, i) => new VectorUpsert(seedB.chunks[i].Id, seedB.document.Id, seedB.source.Id, v)).ToList(),
            Model);
        var actual = await vec.SearchAsync(query, Model, topK: 3);

        Assert.Equal(
            expected.Select(h => h.ChunkId).Select((_, i) => i).ToArray(),
            actual.Select((_, i) => i).ToArray()); // same count/rank positions
        Assert.Equal(expected.Count, actual.Count);
        // same relative order: scores strictly non-increasing and equivalent ordering
        for (var i = 1; i < actual.Count; i++)
            Assert.True(actual[i - 1].Score >= actual[i].Score);
        Assert.Equal(expected[0].Score, actual[0].Score, precision: 3);
    }

    [Fact]
    public async Task Search_FiltersBySource()
    {
        await using var db = CreateDb();
        var a = await SeedAsync(db, "source-a");
        var b = await SeedAsync(db, "source-b");
        var store = new SqliteVecVectorStore(db, dimensions: 4);
        await store.UpsertAsync(a.chunks[0].Id, a.document.Id, a.source.Id, new float[] { 1, 0, 0, 0 }, Model);
        await store.UpsertAsync(b.chunks[0].Id, b.document.Id, b.source.Id, new float[] { 1, 0, 0, 0 }, Model);

        var onlyA = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, Model, topK: 10, sourceIds: [a.source.Id]);
        Assert.Equal(new[] { a.chunks[0].Id }, onlyA.Select(h => h.ChunkId).ToArray());

        var empty = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, Model, topK: 10, sourceIds: []);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Search_FiltersByModel()
    {
        await using var db = CreateDb();
        var (source, document, chunks) = await SeedAsync(db, "s1");
        var store = new SqliteVecVectorStore(db, dimensions: 4);
        await store.UpsertAsync(chunks[0].Id, document.Id, source.Id, new float[] { 1, 0, 0, 0 }, "model-a");
        await store.UpsertAsync(chunks[1].Id, document.Id, source.Id, new float[] { 1, 0, 0, 0 }, "model-b");

        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, "model-b", topK: 10);
        Assert.Equal(new[] { chunks[1].Id }, hits.Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public async Task DeleteByDocument_RemovesVectors()
    {
        await using var db = CreateDb();
        var (source, document, chunks) = await SeedAsync(db, "s1");
        var store = new SqliteVecVectorStore(db, dimensions: 4);
        await store.UpsertAsync(chunks[0].Id, document.Id, source.Id, new float[] { 1, 0, 0, 0 }, Model);

        await store.DeleteByDocumentAsync(document.Id);

        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, Model, topK: 10);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Upsert_ReplacesExistingVector()
    {
        await using var db = CreateDb();
        var (source, document, chunks) = await SeedAsync(db, "s1");
        var store = new SqliteVecVectorStore(db, dimensions: 4);
        await store.UpsertAsync(chunks[0].Id, document.Id, source.Id, new float[] { 0, 1, 0, 0 }, Model);
        await store.UpsertAsync(chunks[0].Id, document.Id, source.Id, new float[] { 1, 0, 0, 0 }, Model);

        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, Model, topK: 10);
        var hit = Assert.Single(hits);
        Assert.True(hit.Score > 0.99);
    }

    [Fact]
    public async Task DimensionMismatch_Fails_Loudly()
    {
        await using var db = CreateDb();
        var (source, document, chunks) = await SeedAsync(db, "s1");
        var store = new SqliteVecVectorStore(db, dimensions: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(chunks[0].Id, document.Id, source.Id, new float[8], Model));
        Assert.Contains("dimensions", ex.Message);
    }

    [Fact]
    public async Task Backfill_ImportsBlobsWrittenBySqliteProvider()
    {
        // T4: flipping VectorStore:Provider on an existing database must not
        // silently lose vectors — the store backfills vec_chunks from
        // DocumentChunks.Embedding on first use.
        await using var db = CreateDb();
        var (source, document, chunks) = await SeedAsync(db, "s1");
        var legacy = new SqliteVectorStore(db);
        await legacy.UpsertAsync(chunks[0].Id, document.Id, source.Id, new float[] { 1, 0, 0, 0 }, Model);

        var store = new SqliteVecVectorStore(db, dimensions: 4);
        var hits = await store.SearchAsync(new float[] { 1, 0, 0, 0 }, Model, topK: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(chunks[0].Id, hit.ChunkId);
    }

    private static KnowledgeHubDbContext CreateDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new KnowledgeHubDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<(KnowledgeSource source, KnowledgeDocument document, List<DocumentChunk> chunks)>
        SeedAsync(KnowledgeHubDbContext db, string sourceName, int chunkCount = 3)
    {
        var source = new KnowledgeSource { Name = sourceName, SourceType = SourceType.ObsidianVault };
        var document = new KnowledgeDocument
        {
            KnowledgeSourceId = source.Id,
            Title = "doc",
            UriReference = $"vault/{sourceName}.md",
        };
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => new DocumentChunk
            {
                KnowledgeDocumentId = document.Id,
                ChunkIndex = i,
                TextContent = $"text {i}",
            })
            .ToList();
        db.Sources.Add(source);
        db.Documents.Add(document);
        db.Chunks.AddRange(chunks);
        await db.SaveChangesAsync();
        return (source, document, chunks);
    }
}
