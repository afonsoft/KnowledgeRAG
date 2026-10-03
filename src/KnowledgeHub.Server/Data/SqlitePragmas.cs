using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace KnowledgeHub.Server.Data;

/// <summary>
/// Per-connection SQLite performance pragmas (2026-10-03 perf pass).
/// journal_mode=WAL is persisted in the db file by
/// <see cref="DatabaseMigrator"/> — everything here is a session hint and
/// must be re-applied whenever a connection opens.
/// </summary>
internal static class SqlitePragmas
{
    /// <summary>synchronous=NORMAL is safe under WAL and removes a fsync per
    /// commit; temp_store keeps FTS5/RRF sort memory off disk; cache_size
    /// negative = KiB (64 MB page cache); mmap_size maps 256 MB for reads.</summary>
    public const string ConnectionSql = """
        PRAGMA synchronous=NORMAL;
        PRAGMA temp_store=MEMORY;
        PRAGMA cache_size=-65536;
        PRAGMA mmap_size=268435456;
        """;

    /// <summary>Apply on a connection opened outside EF (dedicated clones —
    /// <see cref="SqliteConnectionLease.Dedicated"/> readers). No-op on
    /// non-sqlite connections; failures are ignored by callers (pragmas are
    /// hints, never worth failing a query over).</summary>
    public static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection)
            return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = ConnectionSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Applies <see cref="SqlitePragmas.ConnectionSql"/> every time an
/// EF sqlite connection opens — pooled contexts and factory contexts get the
/// same per-connection hints as the migration connection.</summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        try
        {
            await SqlitePragmas.ApplyAsync(connection, cancellationToken);
        }
        catch (SqliteException)
        {
            // pragma hint failed — the query path must not fail over it
        }
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
