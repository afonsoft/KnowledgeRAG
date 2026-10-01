using KnowledgeHub.Server.VectorStore;
using Xunit;

namespace KnowledgeHub.Tests.Unit.VectorStore;

/// <summary>
/// SPEC-20260923-pgvector-hnsw-scale — unit tests for the testable seams that do
/// not require a live Postgres: dimension guards, empty-batch no-op, DDL shapes.
/// </summary>
public sealed class PostgresVectorStoreTests
{
    private static PostgresVectorStore NewStore(int dimensions = 384, PostgresOptions? options = null) =>
        new("Host=127.0.0.1;Database=unused", dimensions, options);

    [Fact]
    public async Task UpsertBatch_EmptyList_ReturnsWithoutConnecting()
    {
        // Covers RF-002: empty batch is a no-op — completes without opening a
        // connection (the fake connection string above points at nothing).
        await using var store = NewStore();
        var call = store.UpsertBatchAsync([], "m");
        await call;
        Assert.True(call.IsCompletedSuccessfully); // empty batch returns without connecting
    }

    [Fact]
    public async Task UpsertAsync_WrongDimensions_ThrowsBeforeConnecting()
    {
        await using var store = NewStore(dimensions: 384);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new float[8], "m"));
        Assert.Contains("384", ex.Message);
    }

    [Fact]
    public async Task UpsertBatch_AnyWrongDimensions_ThrowsBeforeConnecting()
    {
        await using var store = NewStore(dimensions: 384);
        var items = new[]
        {
            new VectorUpsert(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new float[384]),
            new VectorUpsert(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new float[8])
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertBatchAsync(items, "m"));
    }

    [Fact]
    public void BuildBatchUpsertSql_GeneratesNRowsOf6Params()
    {
        // Covers RF-002: one multi-row INSERT, 6 positional params per row
        // (metadata jsonb rides the upsert and the conflict update).
        var sql = PostgresVectorStore.BuildBatchUpsertSql(2);
        Assert.Contains("($1,$2,$3,$4,$5,$6),($7,$8,$9,$10,$11,$12)", sql);
        Assert.Contains("ON CONFLICT (chunk_id) DO UPDATE", sql);
        Assert.Contains("EXCLUDED.model", sql);
        Assert.Contains("metadata = EXCLUDED.metadata", sql);
    }

    [Fact]
    public void SerializeMetadata_NullAndEmpty_ProduceEmptyJsonObject()
    {
        // SPEC-20260923-pgvector-metadata-upsert AC: null/empty → '{}'.
        Assert.Equal("{}", PostgresVectorStore.SerializeMetadata(null));
        Assert.Equal("{}",
            PostgresVectorStore.SerializeMetadata(
                new Dictionary<string, string>()));
    }

    [Fact]
    public void SerializeMetadata_Map_ProducesValidJsonObject()
    {
        var json = PostgresVectorStore.SerializeMetadata(new Dictionary<string, string>
        {
            ["sourceType"] = "ObsidianVault",
            ["indexedAt"] = "2026-09-24T01:02:03.0000000Z"
        });
        var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("ObsidianVault",
            parsed.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("2026-09-24T01:02:03.0000000Z",
            parsed.RootElement.GetProperty("indexedAt").GetString());
    }

    [Fact]
    public void BuildHnswIndexSql_UsesCosineOpsAndOptions()
    {
        // Covers RF-001: HNSW index DDL uses vector_cosine_ops and configured params.
        var store = NewStore(options: new PostgresOptions { HnswM = 24, HnswEfConstruction = 96 });
        var sql = store.BuildHnswIndexSql();
        Assert.Contains("hnsw (embedding vector_cosine_ops)", sql);
        Assert.Contains("m = 24", sql);
        Assert.Contains("ef_construction = 96", sql);
        Assert.Contains(PostgresVectorStore.HnswIndexName, sql);
        Assert.DoesNotContain("CONCURRENTLY", sql);
    }

    [Fact]
    public void SecondaryIndexesSql_ContainsRequiredIndexes()
    {
        // Covers SPEC-20260924-pgvector-rag-performance RF-001:
        // secondary indexes on source_id, document_id, compound and metadata GIN
        var sql = PostgresVectorStore.SecondaryIndexesSql;
        Assert.Contains("kh_embeddings_source_idx", sql);
        Assert.Contains("kh_embeddings_document_idx", sql);
        Assert.Contains("kh_embeddings_source_model_idx", sql);
        Assert.Contains("kh_embeddings_metadata_gin_idx", sql);
        Assert.Contains("USING gin (metadata)", sql);
    }

    [Fact]
    public void PostgresOptions_HasOptimizedDefaults()
    {
        // Covers SPEC-20260924-pgvector-rag-performance RF-002, RF-003, RF-004
        var opts = new PostgresOptions();
        Assert.Equal(1000, opts.HnswThreshold);
        Assert.Equal(16, opts.HnswM);
        Assert.Equal(64, opts.HnswEfConstruction);
        Assert.Equal(40, opts.HnswEfSearch);
        Assert.Equal(5, opts.MinPoolSize);
        Assert.Equal(50, opts.MaxPoolSize);
        Assert.Equal(30, opts.CommandTimeoutSeconds);
        Assert.Equal(300, opts.ConnectionIdleLifetimeSeconds);
    }

    [Fact]
    public async Task PostgresVectorStore_InstantiatesWithCustomPoolOptions()
    {
        // Covers SPEC-20260924-pgvector-rag-performance RF-004
        var customOpts = new PostgresOptions
        {
            MinPoolSize = 10,
            MaxPoolSize = 100,
            CommandTimeoutSeconds = 45,
            ConnectionIdleLifetimeSeconds = 600,
            HnswEfSearch = 80
        };
        await using var store = NewStore(options: customOpts);
        Assert.NotNull(store);
    }
}
// SPEC-20260925-pgvector-halfvec + iterative-filtered-scan — DDL/version seams.
public sealed class PostgresHalfvecTests
{
    [Fact]
    public void BuildHnswIndexSql_Halfvec_UsesHalfvecOps()
    {
        var store = new PostgresVectorStore("Host=x", 384,
            new PostgresOptions { StorageType = "halfvec" });
        Assert.Contains("halfvec_cosine_ops", store.BuildHnswIndexSql());
    }

    [Fact]
    public void BuildHnswIndexSql_Default_UsesVectorOps()
    {
        var store = new PostgresVectorStore("Host=x", 384);
        Assert.Contains("vector_cosine_ops", store.BuildHnswIndexSql());
    }

    [Theory]
    [InlineData("0.7", "0.7", true)]
    [InlineData("0.8.1", "0.7", true)]
    [InlineData("0.6", "0.7", false)]
    [InlineData("0.7.0", "0.7", true)]
    public void VersionSupported_ComparesNumeric(string have, string min, bool expected) =>
        Assert.Equal(expected, PostgresVectorStore.VersionSupported(have, min));
}

// SPEC-20260926-pgvector-scan-and-halfvec — iterative-scan GUC name.
public sealed class PgIterativeScanTests
{
    [Fact]
    public void IterativeScan_UsesHnswNamespaceGuc() =>
        // The pgvector≥0.8 GUC is hnsw.iterative_scan — pgvector.iterative_scan
        // parses but is never honored, which made the opt-in a no-op.
        Assert.Equal("hnsw.iterative_scan", PostgresVectorStore.IterativeScanGuc);
}
