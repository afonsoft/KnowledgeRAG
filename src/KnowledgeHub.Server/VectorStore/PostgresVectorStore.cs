using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace KnowledgeHub.Server.VectorStore;

/// <summary>
/// pgvector-backed store (SPEC-02 RF-006). Catalog/documents stay on SQLite —
/// only embeddings live in Postgres (kh_embeddings table), ranked server-side
/// with the &lt;=&gt; cosine-distance operator.
/// SPEC-20260923-pgvector-hnsw-scale: conditional HNSW index once the table
/// crosses <see cref="PostgresOptions.HnswThreshold"/> (exact scan is better
/// below it), batched upserts, and a <c>metadata jsonb</c> column reserved for
/// SQL-side filtering (SPEC-20260923-retrieval-quality).
/// </summary>
public sealed class PostgresVectorStore : IVectorStore, IAsyncDisposable
{
    private const string StorageVector = "vector";
    private const string StorageHalfvec = "halfvec";

    internal const string HnswIndexName = "kh_embeddings_embedding_hnsw_idx";

    /// <summary>SPEC-20260926-pgvector-scan-and-halfvec RF-001: the iterative
    /// filtered-scan GUC lives under the <c>hnsw.</c> namespace —
    /// <c>pgvector.iterative_scan</c> is accepted silently but never honored.</summary>
    internal const string IterativeScanGuc = "hnsw.iterative_scan";

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _dimensions;
    public int? Dimensions => _dimensions;
    private readonly PostgresOptions _options;
    private bool _initialized;
    private volatile bool _hnswIndexCreated;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    /// <summary>Effective storage flavour — <c>"halfvec"</c> only after the
    /// version check passes (SPEC-20260925-pgvector-halfvec RF-002).</summary>
    private string _storageType = StorageVector;

    public PostgresVectorStore(string connectionString, int dimensions = 384, PostgresOptions? options = null)
    {
        _options = options ?? new PostgresOptions();
        _storageType = options is null ? StorageVector : _options.StorageType;
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MinPoolSize = _options.MinPoolSize,
            MaxPoolSize = _options.MaxPoolSize,
            ConnectionIdleLifetime = _options.ConnectionIdleLifetimeSeconds,
            CommandTimeout = _options.CommandTimeoutSeconds
        };
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        builder.UseVector();
        _dataSource = builder.Build();
        _dimensions = dimensions;
    }

    /// <summary>Serializes the metadata map for the jsonb column — null/empty →
    /// <c>{}</c> (SPEC-20260923-pgvector-metadata-upsert RF-002).</summary>
    internal static string SerializeMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null || metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    public async Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        CheckDimensions(vector);
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kh_embeddings (chunk_id, document_id, source_id, model, embedding, metadata)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (chunk_id) DO UPDATE SET model = $4, embedding = $5, metadata = $6
            """;
        cmd.Parameters.AddWithValue(chunkId);
        cmd.Parameters.AddWithValue(documentId);
        cmd.Parameters.AddWithValue(sourceId);
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.Add(VectorParameter(vector));
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, SerializeMetadata(metadata));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // SPEC-20260924-pgvector-rag-performance RF-002: auto-create HNSW index once threshold is reached
        if (!_hnswIndexCreated)
            await MaybeCreateHnswIndexAsync(conn, cancellationToken);
    }

    /// <summary>
    /// SPEC-20260923-pgvector-hnsw-scale RF-002: one multi-row INSERT per batch
    /// (≤ <see cref="PostgresOptions.BatchMax"/> rows per command) instead of the
    /// interface default's per-item round-trips.
    /// </summary>
    public async Task UpsertBatchAsync(
        IReadOnlyList<VectorUpsert> items, string model, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;
        foreach (var item in items)
            CheckDimensions(item.Vector);

        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            foreach (var batch in items.Chunk(_options.BatchMax))
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = BuildBatchUpsertSql(batch.Length);
                foreach (var item in batch)
                {
                    cmd.Parameters.AddWithValue(item.ChunkId);
                    cmd.Parameters.AddWithValue(item.DocumentId);
                    cmd.Parameters.AddWithValue(item.SourceId);
                    cmd.Parameters.AddWithValue(model);
                    cmd.Parameters.Add(VectorParameter(item.Vector));
                    cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, SerializeMetadata(item.Metadata));
                }
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            committed = true;

            // SPEC-20260925-pgvector-source-cascade RF-003: refresh planner stats
            // after large bulk writes — the biggest table churn.
            if (items.Count >= _options.AnalyzeThresholdRows)
                await AnalyzeAsync(conn, cancellationToken);

            // SPEC-20260924-pgvector-rag-performance RF-002: auto-create HNSW index once threshold is reached
            if (!_hnswIndexCreated)
                await MaybeCreateHnswIndexAsync(conn, cancellationToken);
        }
        catch
        {
            // RF-009 (SPEC-20260926-ingestion-connector-integrity): post-commit
            // ANALYZE/HNSW failures must not roll back — the transaction is
            // already committed, rollback would throw and mark a persisted
            // batch as failed.
            if (!committed)
                await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>Multi-row INSERT with 6 positional parameters per row.</summary>
    internal static string BuildBatchUpsertSql(int rowCount)
    {
        var sb = new System.Text.StringBuilder(
            "INSERT INTO kh_embeddings (chunk_id, document_id, source_id, model, embedding, metadata) VALUES ");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0)
                sb.Append(',');
            var b = i * 6;
            sb.Append($"(${b + 1},${b + 2},${b + 3},${b + 4},${b + 5},${b + 6})");
        }
        sb.Append(
            " ON CONFLICT (chunk_id) DO UPDATE SET model = EXCLUDED.model, embedding = EXCLUDED.embedding," +
            " document_id = EXCLUDED.document_id, source_id = EXCLUDED.source_id, metadata = EXCLUDED.metadata");
        return sb.ToString();
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // chunk ids embed the document id via a companion table keyed by document
        cmd.CommandText = "DELETE FROM kh_embeddings WHERE document_id = $1";
        cmd.Parameters.AddWithValue(documentId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>SPEC-20260925-pgvector-source-cascade RF-001: EF cascade on
    /// <c>Sources → Documents → Chunks</c> never reaches this external table —
    /// source deletion must purge its vectors explicitly or they orphan.</summary>
    public async Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM kh_embeddings WHERE source_id = $1";
        cmd.Parameters.AddWithValue(sourceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryVector, string model, int topK,
        IReadOnlyCollection<Guid>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (sourceIds is { Count: 0 })
            return [];

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            // SPEC-20260924-pgvector-rag-performance RF-003: tune hnsw.ef_search for query speed vs recall
            if (_options.HnswEfSearch > 0)
            {
                try
                {
                    await using var setCmd = conn.CreateCommand();
                    setCmd.Transaction = tx;
                    setCmd.CommandText = $"SET LOCAL hnsw.ef_search = {_options.HnswEfSearch}"; // NOSONAR S2077 — SET não aceita parâmetros; valor int interno
                    await setCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException)
                {
                    // Extension version might not support hnsw.ef_search or index absent — safe ignore
                }
            }

            // SPEC-20260925-pgvector-iterative-filtered-scan RF-001/RF-002: a
            // selective source_id filter starves the HNSW recall — relaxed
            // iteration keeps scanning past the first topK until enough rows
            // survive the filter. Opt-in; ignored when the extension predates
            // pgvector 0.8 (PostgresException swallowed like ef_search above).
            if (_options.IterativeScan && sourceIds is { Count: > 0 })
            {
                try
                {
                    await using var setCmd = conn.CreateCommand();
                    setCmd.Transaction = tx;
                    setCmd.CommandText = $"SET LOCAL {IterativeScanGuc} = relaxed_order"; // NOSONAR S2077 — nome de GUC é const interno
                    await setCmd.ExecuteNonQueryAsync(cancellationToken);

                    if (_options.IterativeScanMaxTuples > 0)
                    {
                        await using var maxCmd = conn.CreateCommand();
                        maxCmd.Transaction = tx;
                        maxCmd.CommandText = $"SET LOCAL hnsw.max_scan_tuples = {_options.IterativeScanMaxTuples}"; // NOSONAR S2077 — SET não aceita parâmetros; valor int interno
                        await maxCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                catch (PostgresException)
                {
                    // pgvector < 0.8 — ignore, filtered post-scan still applies.
                }
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var whereSource = sourceIds is null ? "" : " AND source_id = ANY($4)";
            cmd.CommandText = // NOSONAR S2077 — interpola apenas cláusula estática "AND source_id = ANY($4)"; valores parametrizados
                $"""
                SELECT chunk_id, 1 - (embedding <=> $1) AS score
                FROM kh_embeddings
                WHERE model = $2{whereSource}
                ORDER BY embedding <=> $1
                LIMIT $3
                """;
            cmd.Parameters.Add(VectorParameter(queryVector));
            cmd.Parameters.AddWithValue(model);
            cmd.Parameters.AddWithValue(topK);
            if (sourceIds is not null)
                cmd.Parameters.AddWithValue(sourceIds.ToArray());

            var hits = new List<VectorHit>();
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    hits.Add(new VectorHit(
                        reader.GetGuid(0),
                        reader.GetDouble(1)));
                }
            }

            // RF-501 (SPEC-20260926-review-backlog-remediation): relaxed_order
            // may emit hits out of distance order — the IVectorStore contract
            // is score-sorted output; re-sort before returning.
            hits.Sort(static (a, b) => b.Score.CompareTo(a.Score));

            await tx.CommitAsync(cancellationToken);
            return hits;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>RF-004 (halfvec): ops class matching the effective column
    /// type — <c>halfvec_cosine_ops</c> on halfvec, <c>vector_cosine_ops</c>
    /// otherwise.</summary>
    private string OpsClass =>
        _storageType == StorageHalfvec ? "halfvec_cosine_ops" : "vector_cosine_ops";

    /// <summary>
    /// RF-001: HNSW DDL — interpolated ints only (identifiers can't be parameters).
    /// Runs as a standalone command; CONCURRENTLY is intentionally not used because
    /// it cannot run inside the init batch and the store owns this DB exclusively.
    /// </summary>
    internal string BuildHnswIndexSql() => $"""
        CREATE INDEX IF NOT EXISTS {HnswIndexName}
        ON kh_embeddings USING hnsw (embedding {OpsClass})
        WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction})
        """;

    /// <summary>
    /// SPEC-20260924-pgvector-rag-performance RF-001: Secondary support indexes on
    /// source_id, document_id, compound (source_id, model) and metadata jsonb (GIN).
    /// </summary>
    internal static string SecondaryIndexesSql => """
        CREATE INDEX IF NOT EXISTS kh_embeddings_source_idx ON kh_embeddings (source_id);
        CREATE INDEX IF NOT EXISTS kh_embeddings_document_idx ON kh_embeddings (document_id);
        CREATE INDEX IF NOT EXISTS kh_embeddings_source_model_idx ON kh_embeddings (source_id, model);
        CREATE INDEX IF NOT EXISTS kh_embeddings_metadata_gin_idx ON kh_embeddings USING gin (metadata);
        """;

    private void CheckDimensions(float[] vector)
    {
        if (vector.Length != _dimensions)
            throw new ArgumentException(
                $"Vector dimension {vector.Length} does not match configured {_dimensions}",
                nameof(vector));
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await _initGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            // SPEC-20260926-pgvector-scan-and-halfvec RF-003: the extension must
            // exist BEFORE ResolveStorageTypeAsync reads extversion — on a fresh
            // database the lookup otherwise finds nothing and halfvec silently
            // degrades to vector, ignoring the opt-in.
            await using (var ext = conn.CreateCommand())
            {
                ext.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
                await ext.ExecuteNonQueryAsync(cancellationToken);
            }

            // SPEC-20260926-pgvector-live-tests RF-002: on a fresh database this
            // connection's type map was loaded before the extension existed —
            // pooled reuse then fails with "Cannot resolve 'vector'". Reload so
            // the pgvector handlers resolve on this and future physical conns.
            await conn.ReloadTypesAsync(cancellationToken);

            // SPEC-20260925-pgvector-halfvec RF-002: resolve the effective
            // storage type BEFORE the DDL — halfvec needs pgvector ≥0.7 and
            // dims ≤2000; otherwise fall back to vector with a warning.
            await ResolveStorageTypeAsync(conn, cancellationToken);

            await using var cmd = conn.CreateCommand();
            // RF-003: metadata jsonb — additive on existing DBs via ALTER … IF NOT EXISTS.
            // NOSONAR pragma: DDL interpola apenas _storageType (enum interno validado)
#pragma warning disable S2077
            cmd.CommandText = $$"""
                CREATE TABLE IF NOT EXISTS kh_embeddings (
                    chunk_id    uuid PRIMARY KEY,
                    document_id uuid NOT NULL,
                    source_id   uuid NOT NULL,
                    model       text NOT NULL,
                    embedding   {{_storageType}}({{_dimensions}}) NOT NULL
                );
                ALTER TABLE kh_embeddings ADD COLUMN IF NOT EXISTS metadata jsonb NOT NULL DEFAULT '{}'::jsonb;
                CREATE INDEX IF NOT EXISTS kh_embeddings_model_idx ON kh_embeddings (model);
                CREATE INDEX IF NOT EXISTS kh_embeddings_source_idx ON kh_embeddings (source_id);
                CREATE INDEX IF NOT EXISTS kh_embeddings_document_idx ON kh_embeddings (document_id);
                CREATE INDEX IF NOT EXISTS kh_embeddings_source_model_idx ON kh_embeddings (source_id, model);
                CREATE INDEX IF NOT EXISTS kh_embeddings_metadata_gin_idx ON kh_embeddings USING gin (metadata);
                """;
#pragma warning restore S2077
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await MaybeMigrateStorageAsync(conn, cancellationToken);
            await MaybeCreateHnswIndexAsync(conn, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>RF-002 (halfvec): reads pgvector's version and the existing
    /// column type; resolves the effective flavour and fails fast on the
    /// 2000-dim halfvec cap.</summary>
    private async Task ResolveStorageTypeAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var requested = _options.StorageType.ToLowerInvariant();
        if (requested is not (StorageVector or StorageHalfvec))
            throw new InvalidOperationException(
                $"VectorStore:Postgres:StorageType must be 'vector' or 'halfvec' (got '{_options.StorageType}')");

        if (requested == StorageHalfvec && _dimensions > 2000)
            throw new InvalidOperationException(
                $"halfvec supports up to 2000 dims for indexed columns — configured {_dimensions}. " +
                "Use StorageType=vector.");

        if (requested == StorageVector)
        {
            _storageType = StorageVector;
            return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extversion FROM pg_extension WHERE extname='vector'";
        var extVersion = await cmd.ExecuteScalarAsync(ct) as string;
        if (extVersion is null || !VersionSupported(extVersion, "0.7"))
        {
            _storageType = StorageVector; // degrade silently — writes keep working
            return;
        }
        _storageType = StorageHalfvec;
    }

    /// <summary>"0.8.1" vs "0.7" — permissive numeric compare.</summary>
    internal static bool VersionSupported(string extVersion, string min)
    {
        var a = extVersion.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToList();
        var b = min.Split('.').Select(int.Parse).ToList();
        for (var i = 0; i < b.Count; i++)
            if ((i < a.Count ? a[i] : 0) != b[i])
                return (i < a.Count ? a[i] : 0) > b[i];
        return true;
    }

    /// <summary>RF-003: column type mismatch on an existing table —
    /// <c>ALTER … TYPE halfvec</c> rewrites the whole table, so it only runs
    /// behind <see cref="PostgresOptions.AllowStorageMigration"/>.</summary>
    private async Task MaybeMigrateStorageAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT udt_name FROM information_schema.columns
            WHERE table_name='kh_embeddings' AND column_name='embedding'
            """;
        var actual = await cmd.ExecuteScalarAsync(ct) as string;
        if (actual is null || actual == _storageType)
            return;

        if (!_options.AllowStorageMigration)
            return; // mismatch tolerated: queries work across vector/halfvec

        // RF-002 (SPEC-20260926-pgvector-scan-and-halfvec): the HNSW index
        // holds a vector ops class — ALTER TYPE would fail trying to rebuild
        // it for halfvec, so drop BEFORE the column-type change and let the
        // threshold logic recreate it with the right ops.
        // RF-502 (SPEC-20260926-review-backlog-remediation): DROP+ALTER in one
        // transaction — a failed ALTER previously left the table WITHOUT the
        // index (the drop had already committed) for every other instance.
        _hnswIndexCreated = false;
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var drop = conn.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = $"DROP INDEX IF EXISTS {HnswIndexName}";
                await drop.ExecuteNonQueryAsync(ct);
            }

            await using var alter = conn.CreateCommand();
            alter.Transaction = tx;
            alter.CommandTimeout = 600; // table rewrite — give it room
            alter.CommandText = // NOSONAR S2077 — _storageType é enum interno validado (vector|halfvec)
                $"ALTER TABLE kh_embeddings ALTER COLUMN embedding TYPE {_storageType} USING embedding::{_storageType}";
            await alter.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Parameter for the embedding column — HalfVector when the
    /// storage type resolved to halfvec (SPEC-20260925-pgvector-halfvec RF-001).</summary>
    private NpgsqlParameter VectorParameter(float[] v) =>
        _storageType == StorageHalfvec
            ? new NpgsqlParameter
            {
                Value = new HalfVector(new ReadOnlyMemory<Half>(v.Select(f => (Half)f).ToArray())),
                DataTypeName = StorageHalfvec
            }
            : new NpgsqlParameter { Value = new Vector(v) };

    /// <summary>
    /// RF-001: creates the HNSW index only when the row count crosses the
    /// configured threshold. Failure to create it (e.g. pgvector &lt;0.5) is a
    /// warning — exact search keeps working.
    /// </summary>
    private async Task MaybeCreateHnswIndexAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_hnswIndexCreated)
            return;

        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT count(*) FROM kh_embeddings";
        var rows = (long)(await count.ExecuteScalarAsync(ct))!;
        if (rows < _options.HnswThreshold)
            return;

        try
        {
            await using var idx = conn.CreateCommand();
            idx.CommandText = BuildHnswIndexSql();
            await idx.ExecuteNonQueryAsync(ct);
            _hnswIndexCreated = true;
        }
        catch (PostgresException)
        {
            // Extension too old for hnsw / index build refused — exact scan stays.
        }
    }

    /// <summary>RF-003: ANALYZE after bulk upserts — planner stats go stale
    /// exactly when the table changed the most.</summary>
    private static async Task AnalyzeAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "ANALYZE kh_embeddings";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>RF-003: weekly VACUUM ANALYZE via MaintenanceBackgroundService —
    /// the store owns this DB exclusively (non-concurrent VACUUM is safe).</summary>
    public async Task VacuumAnalyzeAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM ANALYZE kh_embeddings";
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>RF-004: diagnostics for /api/diagnostics/vectorstore.</summary>
    public async Task<object> GetDiagnosticsAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*),
                   pg_size_pretty(pg_total_relation_size('kh_embeddings')),
                   EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = $1),
                   (SELECT extversion FROM pg_extension WHERE extname = 'vector')
            FROM kh_embeddings
            """;
        cmd.Parameters.AddWithValue(HnswIndexName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new { provider = "postgres", error = "no result" };
        return new
        {
            provider = "postgres",
            // Connection identity without credentials: host[:port] + database.
            host = conn.DataSource,
            database = conn.Database,
            // SPEC-20260926-review-docs-and-misc RF-003: expose the effective
            // storage flavour (vector|halfvec) promised by the API docs.
            storageType = _storageType,
            rows = reader.GetInt64(0),
            size = reader.GetString(1),
            hnswIndex = reader.GetBoolean(2),
            pgvectorVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
            dimensions = _dimensions
        };
    }

    public async ValueTask DisposeAsync()
    {
        _initGate.Dispose();
        await _dataSource.DisposeAsync();
    }
}
