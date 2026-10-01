using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using KnowledgeHub.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Search;

/// <summary>One FTS5 hit: chunk id + 1-based rank + raw bm25 score.</summary>
public sealed record LexicalHit(Guid ChunkId, int Rank, double Bm25);

/// <summary>
/// SQLite FTS5 lexical index over <c>DocumentChunks</c>
/// (SPEC-20260914-hybrid-retrieval RF-001/RF-004). The <c>chunks_fts</c> virtual
/// table is created and backfilled by the <c>AddChunksFts</c> migration and kept
/// in sync by <see cref="ReconcileAsync"/> on every ingestion write path.
/// </summary>
public interface ILexicalSearchService
{
    /// <summary>False when lexical search is disabled or the vector store is postgres.</summary>
    bool Enabled { get; }

    /// <summary>bm25-ranked chunk ids; empty on syntax/host errors (never throws to callers).</summary>
    Task<IReadOnlyList<LexicalHit>> SearchAsync(
        string query, int topK, IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken = default);

    /// <summary>Drops FTS rows whose chunk no longer exists and inserts rows for chunks never indexed.</summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

public sealed class LexicalSearchService(
    KnowledgeHubDbContext db,
    IConfiguration configuration,
    CatalogDatabase catalog,
    ILogger<LexicalSearchService> logger) : ILexicalSearchService
{
    private const string TableName = "chunks_fts";

    public bool Enabled { get; } =
        configuration.GetValue("Search:Lexical:Enabled", true);

    public async Task<IReadOnlyList<LexicalHit>> SearchAsync(
        string query, int topK, IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken = default)
    {
        var match = FtsQuerySanitizer.ToMatchExpression(query);
        if (match is null || !Enabled)
            return [];

        // SPEC-20260926-unified-database-provider RF-003: Postgres catalog uses
        // the generated search_vector tsvector column (GIN-indexed, self-synced)
        // instead of the chunks_fts virtual table.
        if (catalog.IsPostgres)
            return await SearchPostgresAsync(query, topK, sourceIds, cancellationToken);

        // SPEC-20260926-search-correctness-and-stream RF-001: expansion issues
        // N concurrent calls; the scoped DbContext shares ONE SqliteConnection
        // that cannot serve concurrent readers — dedicated clone for file DBs,
        // per-connection gate for :memory:.
        var efConnection = db.Database.GetDbConnection();
        if (Data.SqliteConnectionLease.Dedicated(efConnection) is { } dedicated)
        {
            try
            {
                await using (dedicated)
                {
                    await dedicated.OpenAsync(cancellationToken);
                    if (!await IsAvailableAsync(dedicated, cancellationToken))
                        return [];
                    return await SearchOnAsync(dedicated, match, topK, sourceIds, cancellationToken);
                }
            }
            catch (SqliteException ex)
            {
                logger.LogWarning(ex, "Lexical search failed on dedicated connection — falling back to empty result set");
                return [];
            }
        }

        var gate = Data.SqliteConnectionLease.GateFor(efConnection);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsAvailableAsync(efConnection, cancellationToken))
                return [];
            var wasOpen = efConnection.State == ConnectionState.Open;
            if (!wasOpen)
                await efConnection.OpenAsync(cancellationToken);
            try
            {
                return await SearchOnAsync(efConnection, match, topK, sourceIds, cancellationToken);
            }
            finally
            {
                if (!wasOpen)
                    await efConnection.CloseAsync();
            }
        }
        catch (SqliteException ex)
        {
            // FTS syntax/IO errors degrade to "no lexical hits" — never a 500.
            logger.LogWarning(ex, "Lexical search failed for match expression — falling back to empty result set");
            return [];
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IReadOnlyList<LexicalHit>> SearchOnAsync(
        DbConnection connection, string match, int topK,
        IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken)
    {
        {
            await using var command = connection.CreateCommand();
            var sourceFilter = "";
            if (sourceIds is { Count: > 0 })
            {
                var names = new List<string>(sourceIds.Count);
                var i = 0;
                foreach (var id in sourceIds)
                {
                    var name = $"$s{i++}";
                    names.Add(name);
                    AddParameter(command, name, id.ToString());
                }
                // EF Core stores TEXT Guids uppercase; NOCASE makes the filter format-agnostic.
                sourceFilter = $"AND d.KnowledgeSourceId COLLATE NOCASE IN ({string.Join(", ", names)}) ";
            }
            else if (sourceIds is { Count: 0 })
            {
                return [];
            }

            command.CommandText = $"""
                SELECT c.Id, f.rank
                FROM {TableName} f
                JOIN Chunks c ON c.Id = f.chunk_id
                JOIN Documents d ON c.KnowledgeDocumentId = d.Id
                WHERE f.text MATCH $match {sourceFilter}
                ORDER BY f.rank
                LIMIT $limit
                """; // NOSONAR S2077 — interpola apenas identificador const TableName e fragmento de placeholders nomeados
            AddParameter(command, "$match", match);
            AddParameter(command, "$limit", topK);

            var hits = new List<LexicalHit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new LexicalHit(
                    Guid.Parse(reader.GetString(0)),
                    hits.Count + 1,
                    reader.GetDouble(1)));
            }
            return hits;
        }
    }

    /// <summary>
    /// Postgres path (SPEC-20260926-unified-database-provider RF-003):
    /// <c>websearch_to_tsquery</c> over the generated <c>search_vector</c> column,
    /// ranked by <c>ts_rank</c>. The column is a stored generated column over
    /// <c>COALESCE(EnrichedText, TextContent)</c> — always in sync, no reconcile.
    /// Fails soft to an empty result set, never a 500.
    /// </summary>
    private async Task<IReadOnlyList<LexicalHit>> SearchPostgresAsync(
        string query, int topK, IReadOnlyCollection<Guid>? sourceIds, CancellationToken ct)
    {
        if (sourceIds is { Count: 0 })
            return [];
        if (catalog.PostgresConnectionString is not { } cs)
            return [];

        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            var sourceFilter = sourceIds is { Count: > 0 }
                ? "AND d.\"KnowledgeSourceId\" = ANY($3) "
                : "";
            cmd.CommandText = $"""
                SELECT c."Id", ts_rank(c."search_vector", q) AS rank
                FROM "Chunks" c
                JOIN "Documents" d ON c."KnowledgeDocumentId" = d."Id",
                     websearch_to_tsquery('simple', $1) q
                WHERE c."search_vector" @@ q {sourceFilter}
                ORDER BY rank DESC
                LIMIT $2
                """; // NOSONAR S2077 — interpola apenas fragmento de placeholders nomeados
            cmd.Parameters.AddWithValue(query);
            cmd.Parameters.AddWithValue(topK);
            if (sourceIds is { Count: > 0 })
                cmd.Parameters.AddWithValue(sourceIds.ToArray());

            var hits = new List<LexicalHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hits.Add(new LexicalHit(
                    reader.GetGuid(0),
                    hits.Count + 1,
                    reader.GetDouble(1)));
            }
            return hits;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Postgres lexical search failed — falling back to empty result set");
            return [];
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        // Postgres search_vector is a stored generated column — always in sync.
        if (catalog.IsPostgres)
            return;
        if (!Enabled || !await IsAvailableAsync(cancellationToken))
            return;

        await db.Database.ExecuteSqlRawAsync(
            $"""
            DELETE FROM {TableName}
            WHERE chunk_id NOT IN (SELECT Id FROM Chunks);
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO {TableName} (chunk_id, text)
            SELECT Id, COALESCE(EnrichedText, TextContent) FROM Chunks
            WHERE Id NOT IN (SELECT chunk_id FROM {TableName});
            """, cancellationToken);
    }

    // SPEC-20260916-performance-memory-cache H5/RF-006: the FTS table is created
    // by the AddChunksFts migration and never dropped at runtime — cache the
    // probe per data source so every search doesn't re-query sqlite_master.
    // Only `true` is cached: a `false` may be a pre-migration DB that migrates
    // later in the same process (tests create fresh files per case).
    private static readonly ConcurrentDictionary<string, bool> FtsAvailableByDataSource = new();

    private Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        IsAvailableAsync(db.Database.GetDbConnection(), cancellationToken);

    private static async Task<bool> IsAvailableAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is SqliteConnection sqlite
            && FtsAvailableByDataSource.TryGetValue(sqlite.DataSource, out var known)
            && known)
            return true;

        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
            AddParameter(command, "$name", TableName);
            var available = await command.ExecuteScalarAsync(cancellationToken) is not null;
            if (available && connection is SqliteConnection sqliteConn)
                FtsAvailableByDataSource[sqliteConn.DataSource] = true;
            return available;
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
