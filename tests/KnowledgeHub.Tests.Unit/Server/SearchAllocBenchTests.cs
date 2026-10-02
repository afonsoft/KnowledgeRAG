using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace KnowledgeHub.Tests.Unit.Server;

// CA-001 evidence for SPEC-20260916-performance-memory-cache: allocation of the
// old materialize-everything path vs the streaming+heap path over the same data.
[Collection("SearchTelemetry")]
public sealed class SearchAllocBenchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Measure_SearchAllocation_OldVsNew()
    {
        const int chunks = 5000;
        const int dims = 384;

        var dbPath = Path.Join(Path.GetTempPath(), $"kh-bench-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            await using var db = new KnowledgeHubDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault };
            var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
            db.Sources.Add(source);
            db.Documents.Add(doc);
            var rng = new Random(42);
            for (var i = 0; i < chunks; i++)
            {
                var v = new float[dims];
                for (var j = 0; j < dims; j++) v[j] = (float)rng.NextDouble();
                db.Chunks.Add(new DocumentChunk
                {
                    KnowledgeDocumentId = doc.Id,
                    ChunkIndex = i,
                    TextContent = $"t{i}",
                    Embedding = EmbeddingVectorCodec.ToBytes(v),
                    EmbeddingModel = "m"
                });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var query = new float[dims];
            query[0] = 1f;
            const int topK = 10;

            // warm both paths
            await OldPath(db, query, topK);
            await new SqliteVectorStore(db).SearchAsync(query, "m", topK);

            var oldAlloc = await Measure(() => OldPath(db, query, topK));
            var newAlloc = await Measure(() => new SqliteVectorStore(db).SearchAsync(query, "m", topK));

            var report = $"chunks={chunks} dims={dims} topK={topK} | old(allocated)={oldAlloc:N0}B new(allocated)={newAlloc:N0}B ratio={oldAlloc / (double)Math.Max(newAlloc, 1):F1}x";
            output.WriteLine(report);
            await File.WriteAllTextAsync("/tmp/kh-bench.txt", report);
            Assert.True(oldAlloc > 0 && newAlloc > 0, "allocation measurements must be positive");
        }
        finally
        {
            try { File.Delete(dbPath); } catch (Exception ex) { _ = ex.Message; }
        }
    }

    private static async Task<long> Measure(Func<Task<IReadOnlyList<VectorHit>>> run)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        await run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The pre-change implementation: materialize all rows, decode each vector,
    // sort in memory.
    private static async Task<IReadOnlyList<VectorHit>> OldPath(
        KnowledgeHubDbContext db, float[] queryVector, int topK)
    {
        var rows = await db.Chunks
            .Where(c => c.Embedding != null && c.EmbeddingModel == "m")
            .Join(db.Documents, c => c.KnowledgeDocumentId, d => d.Id, (c, d) => new { c.Id, c.Embedding, d.KnowledgeSourceId })
            .AsNoTracking().ToListAsync();

        return rows
            .Select(r => new VectorHit(r.Id, EmbeddingVectorCodec.CosineSimilarity(queryVector, EmbeddingVectorCodec.FromBytes(r.Embedding!))))
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }
}
