using System.Data;
using System.Text.RegularExpressions;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.VectorStore;

/// <summary>
/// SPEC-20260917-sqlite-vec-search: opt-in store backed by the sqlite-vec
/// <c>vec0</c> virtual table — native KNN (cosine distance) replaces the
/// in-process cosine scan of <see cref="SqliteVectorStore"/>. vec_chunks is
/// the single source of truth for this provider, like kh_embeddings is for
/// postgres; on first use it is backfilled from any BLOBs previously written
/// by the sqlite provider so existing deployments migrate without re-ingest.
/// </summary>
public sealed class SqliteVecVectorStore : IVectorStore
{
    private const string TableName = "vec_chunks";
    private static readonly Regex DeclaredDimensions =
        new(@"float\[(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly KnowledgeHubDbContext _db;
    private readonly int _dimensions;
    public int? Dimensions => _dimensions;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public SqliteVecVectorStore(KnowledgeHubDbContext db, int dimensions = 384)
    {
        _db = db;
        _dimensions = dimensions;
    }

    public async Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        CheckDimensions(vector);
        var conn = await VecConnectionAsync(cancellationToken);
        var chunkExists = await _db.Chunks.AnyAsync(c => c.Id == chunkId, cancellationToken);
        if (!chunkExists)
            throw new InvalidOperationException($"Chunk {chunkId} not found");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {TableName} WHERE chunk_id = $id;
            INSERT INTO {TableName} (chunk_id, document_id, source_id, model, embedding)
            VALUES ($id, $doc, $src, $model, $vec);
            """;
        cmd.Parameters.AddWithValue("$id", chunkId.ToString());
        cmd.Parameters.AddWithValue("$doc", documentId.ToString());
        cmd.Parameters.AddWithValue("$src", sourceId.ToString());
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$vec", EmbeddingVectorCodec.ToBytes(vector));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertBatchAsync(
        IReadOnlyList<VectorUpsert> items, string model, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;
        var conn = await VecConnectionAsync(cancellationToken);

        var ids = items.Select(i => i.ChunkId).ToList();
        var existing = await _db.Chunks.Where(c => ids.Contains(c.Id))
            .Select(c => c.Id).ToHashSetAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                CheckDimensions(item.Vector);
                if (!existing.Contains(item.ChunkId))
                    throw new InvalidOperationException($"Chunk {item.ChunkId} not found");

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    DELETE FROM {TableName} WHERE chunk_id = $id;
                    INSERT INTO {TableName} (chunk_id, document_id, source_id, model, embedding)
                    VALUES ($id, $doc, $src, $model, $vec);
                    """;
                cmd.Parameters.AddWithValue("$id", item.ChunkId.ToString());
                cmd.Parameters.AddWithValue("$doc", item.DocumentId.ToString());
                cmd.Parameters.AddWithValue("$src", item.SourceId.ToString());
                cmd.Parameters.AddWithValue("$model", model);
                cmd.Parameters.AddWithValue("$vec", EmbeddingVectorCodec.ToBytes(item.Vector));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var conn = await VecConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE document_id = $doc";
        cmd.Parameters.AddWithValue("$doc", documentId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>SPEC-20260925-pgvector-source-cascade RF-001: vec_chunks is a
    /// separate table — purge by source_id when the source is deleted.</summary>
    public async Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var conn = await VecConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE source_id = $src";
        cmd.Parameters.AddWithValue("$src", sourceId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryVector, string model, int topK,
        IReadOnlyCollection<Guid>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        if (topK <= 0 || sourceIds is { Count: 0 })
            return [];
        CheckDimensions(queryVector);

        // SPEC-20260926-search-correctness-and-stream RF-001: query expansion
        // issues N concurrent calls; the scoped DbContext shares ONE
        // SqliteConnection that cannot serve concurrent readers — dedicated
        // clone for file DBs, per-connection gate for :memory:.
        var efConnection = _db.Database.GetDbConnection();
        if (Data.SqliteConnectionLease.Dedicated(efConnection) is { } dedicated)
        {
            await using (dedicated)
            {
                await dedicated.OpenAsync(cancellationToken);
                await EnsureInitializedOnAsync(dedicated, cancellationToken);
                return await QueryAsync(dedicated, queryVector, model, topK, sourceIds, cancellationToken);
            }
        }

        var gate = Data.SqliteConnectionLease.GateFor(efConnection);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var conn = await VecConnectionAsync(cancellationToken);
            return await QueryAsync(conn, queryVector, model, topK, sourceIds, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IReadOnlyList<VectorHit>> QueryAsync(
        SqliteConnection conn, float[] queryVector, string model, int topK,
        IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken)
    {
        // vec0 metadata columns only support '=' in a KNN WHERE — one KNN per
        // source then merge. Global top-K of the filtered set equals the merge
        // of per-source top-Ks, matching the in-process provider exactly.
        var scopes = sourceIds is null
            ? new List<string?> { null }
            : sourceIds.Select(s => (string?)s.ToString()).ToList();

        var best = new Dictionary<Guid, double>();
        var queryBlob = EmbeddingVectorCodec.ToBytes(queryVector);
        foreach (var sourceId in scopes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sourceId is null
                ? $"""
                    SELECT chunk_id, distance FROM {TableName}
                    WHERE embedding MATCH $vec AND k = $k AND model = $model
                    ORDER BY distance
                    """
                : $"""
                    SELECT chunk_id, distance FROM {TableName}
                    WHERE embedding MATCH $vec AND k = $k AND model = $model AND source_id = $src
                    ORDER BY distance
                    """;
            cmd.Parameters.AddWithValue("$vec", queryBlob);
            cmd.Parameters.AddWithValue("$k", topK);
            cmd.Parameters.AddWithValue("$model", model);
            if (sourceId is not null)
                cmd.Parameters.AddWithValue("$src", sourceId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var chunkId = Guid.Parse(reader.GetString(0));
                var distance = reader.GetDouble(1);
                if (!best.TryGetValue(chunkId, out var current) || distance < current)
                    best[chunkId] = distance;
            }
        }

        return best
            .OrderBy(kv => kv.Value)
            .Take(topK)
            .Select(kv => new VectorHit(kv.Key, 1.0 - kv.Value))
            .ToList();
    }

    private void CheckDimensions(float[] vector)
    {
        if (vector.Length != _dimensions)
            throw new InvalidOperationException(
                $"sqlite-vec: vector has {vector.Length} dimensions but vec_chunks was created for {_dimensions} — " +
                "re-create the index or fix Embeddings:Dimensions");
    }

    /// <summary>Connections that already carry the vec0 module — weak so
    /// dedicated per-search connections do not leak the table.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SqliteConnection, object> VecLoaded = new();

    private static void EnsureVectorLoaded(SqliteConnection conn)
    {
        lock (VecLoaded)
        {
            if (VecLoaded.TryGetValue(conn, out _))
                return;
            conn.LoadVector();
            VecLoaded.Add(conn, new object());
        }
    }

    private async Task<SqliteConnection> VecConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);
        await EnsureInitializedOnAsync(conn, cancellationToken);
        return conn;
    }

    /// <summary>vec0 extension load + schema + backfill on an OPEN connection —
    /// once per service lifetime, whichever connection arrives first.</summary>
    private async Task EnsureInitializedOnAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        // RF-402 (SPEC-20260926-review-backlog-remediation): vec0 is a
        // PER-CONNECTION extension — a write on a different connection after a
        // dedicated-conn search hit "no such module". Load on every new
        // connection; _initialized only gates the schema/backfill.
        EnsureVectorLoaded(conn);
        if (_initialized)
            return;

        await _initGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            await EnsureSchemaAsync(conn, cancellationToken);
            await BackfillFromDocumentChunksAsync(conn, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
            cmd.Parameters.AddWithValue("$name", TableName);
            var ddl = await cmd.ExecuteScalarAsync(cancellationToken) as string;
            if (ddl is not null)
            {
                var match = DeclaredDimensions.Match(ddl);
                if (match.Success && int.Parse(match.Groups[1].Value) != _dimensions)
                    throw new InvalidOperationException(
                        $"sqlite-vec: {TableName} was created with {match.Groups[1].Value} dimensions " +
                        $"but Embeddings:Dimensions is {_dimensions} — drop {TableName} and re-ingest, or align the setting");
                return;
            }
        }

        await using var create = conn.CreateCommand();
        // NOSONAR pragma: TableName é const e _dimensions int interno; identificadores não aceitam parâmetro
#pragma warning disable S2077
        create.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {TableName} USING vec0(
                chunk_id    TEXT PRIMARY KEY,
                document_id TEXT,
                source_id   TEXT,
                model       TEXT,
                embedding   FLOAT[{_dimensions}] distance_metric=cosine
            )
            """;
#pragma warning restore S2077
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One-time import of BLOB vectors written by the in-process
    /// provider, so flipping VectorStore:Provider migrates existing data.</summary>
    private async Task BackfillFromDocumentChunksAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var indexed = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT chunk_id FROM {TableName}";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                indexed.Add(reader.GetString(0));
        }

        var pending = await _db.Chunks
            .Where(c => c.Embedding != null && c.EmbeddingModel != null)
            .Select(c => new { c.Id, c.KnowledgeDocumentId, c.EmbeddingModel, c.Embedding })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var missing = pending.Where(c => !indexed.Contains(c.Id.ToString())).ToList();
        if (missing.Count == 0)
            return;

        var sourceByDocument = await _db.Documents
            .Where(d => missing.Select(c => c.KnowledgeDocumentId).Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.KnowledgeSourceId, cancellationToken);

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var chunk in missing)
            {
                if (!sourceByDocument.TryGetValue(chunk.KnowledgeDocumentId, out var sourceId))
                    continue;
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT INTO {TableName} (chunk_id, document_id, source_id, model, embedding)
                    VALUES ($id, $doc, $src, $model, $vec)
                    """;
                cmd.Parameters.AddWithValue("$id", chunk.Id.ToString());
                cmd.Parameters.AddWithValue("$doc", chunk.KnowledgeDocumentId.ToString());
                cmd.Parameters.AddWithValue("$src", sourceId.ToString());
                cmd.Parameters.AddWithValue("$model", chunk.EmbeddingModel!);
                cmd.Parameters.AddWithValue("$vec", chunk.Embedding!);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
