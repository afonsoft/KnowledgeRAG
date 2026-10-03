using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.VectorStore;

/// <summary>
/// Default zero-infra store: vectors live as BLOBs on <c>DocumentChunks</c>;
/// cosine similarity is computed in-process (SPEC-02 RF-006).
/// </summary>
public sealed class SqliteVectorStore(KnowledgeHubDbContext db) : IVectorStore
{
    public async Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        var chunk = await db.Chunks.FindAsync([chunkId], cancellationToken)
            ?? throw new InvalidOperationException($"Chunk {chunkId} not found");
        chunk.Embedding = EmbeddingVectorCodec.ToBytes(vector);
        chunk.EmbeddingModel = model;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>One chunk lookup + one SaveChanges for the whole batch —
    /// ingestion embeds per-document, so this is the hot path.</summary>
    public async Task UpsertBatchAsync(
        IReadOnlyList<VectorUpsert> items, string model, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        var ids = items.Select(i => i.ChunkId).ToList();
        var chunks = await db.Chunks.Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        foreach (var item in items)
        {
            if (!chunks.TryGetValue(item.ChunkId, out var chunk))
                throw new InvalidOperationException($"Chunk {item.ChunkId} not found");
            chunk.Embedding = EmbeddingVectorCodec.ToBytes(item.Vector);
            chunk.EmbeddingModel = model;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // Perf pass (2026-10-03): single UPDATE instead of loading every chunk
        // into the change tracker and writing one UPDATE per row — same
        // statement shape DeleteBySourceAsync already uses.
        await db.Chunks
            .Where(c => c.KnowledgeDocumentId == documentId && c.Embedding != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Embedding, (byte[]?)null)
                .SetProperty(c => c.EmbeddingModel, (string?)null), cancellationToken);
    }

    /// <summary>SPEC-20260925-pgvector-source-cascade RF-001: embeddings live on
    /// the chunk rows — clear them for every document of the source (the docs
    /// themselves are cascade-deleted by EF on source removal, but reindex-style
    /// callers can purge embeddings without deleting documents).</summary>
    public async Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await db.Chunks
            .Where(c => c.Embedding != null && c.Document.KnowledgeSourceId == sourceId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Embedding, (byte[]?)null)
                .SetProperty(c => c.EmbeddingModel, (string?)null), cancellationToken);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryVector, string model, int topK,
        IReadOnlyCollection<Guid>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
            return [];

        // RF-401 (SPEC-20260926-review-backlog-remediation): the scoped
        // DbContext cannot serve the N concurrent expansion calls — serialize
        // on the connection's gate (a dedicated connection wouldn't help; it
        // is the DbContext itself that disallows concurrent operations).
        var gate = Data.SqliteConnectionLease.GateFor(db.Database.GetDbConnection());
        await gate.WaitAsync(cancellationToken);
        try
        {
            var query = db.Chunks
            .Where(c => c.Embedding != null && c.EmbeddingModel == model)
            .Join(db.Documents, c => c.KnowledgeDocumentId, d => d.Id, (c, d) => new { c.Id, c.Embedding, d.KnowledgeSourceId });

            if (sourceIds is { Count: > 0 })
                query = query.Where(x => sourceIds.Contains(x.KnowledgeSourceId));
            else if (sourceIds is { Count: 0 })
                return [];

            // SPEC-20260916-performance-memory-cache RF-002: stream rows instead of
            // materializing every embedding BLOB, and keep a bounded min-heap of the
            // top-K hits — peak memory is O(topK·dims), not O(N·dims + N·blobBytes).
            var heap = new PriorityQueue<VectorHit, double>(topK);
            await foreach (var row in query.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var score = EmbeddingVectorCodec.CosineSimilarity(queryVector, row.Embedding!);
                if (heap.Count < topK)
                {
                    heap.Enqueue(new VectorHit(row.Id, score), score);
                }
                else if (score > heap.Peek().Score)
                {
                    heap.Dequeue();
                    heap.Enqueue(new VectorHit(row.Id, score), score);
                }
            }

            var hits = new List<VectorHit>(heap.Count);
            while (heap.TryDequeue(out var hit, out _))
                hits.Add(hit);
            hits.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            return hits;
        }
        finally
        {
            gate.Release();
        }
    }
}
