using System.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Data;

/// <summary>
/// Startup schema initialization (SPEC-20260914-efcore-migrations).
/// Applies pending EF Core migrations and baselines databases created in the
/// <c>EnsureCreated</c> era (schema present, no <c>__EFMigrationsHistory</c>)
/// so upgrading never requires data loss.
/// </summary>
public static class DatabaseMigrator
{
    private const string InitialMigrationId = "20260914011858_InitialCreate";
    private const string EfCoreVersion = "10.0.12";

    public static async Task MigrateAsync(KnowledgeHubDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);

        // The EnsureCreated-era guard only applies to SQLite — that era never
        // produced Postgres databases, and sqlite_master doesn't exist there.
        if (db.Database.IsSqlite() && !applied.Any() && await HasTableAsync(db, "Sources", cancellationToken))
        {
            // EnsureCreated-era database: tables exist but there is no history
            // table, so Migrate() would try to re-create them. Record
            // InitialCreate as applied, then let Migrate() take it from there.
            logger.LogInformation(
                "Pre-migrations database detected (schema exists, no migration history) — baselining {MigrationId}.",
                InitialMigrationId);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """,
                cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({InitialMigrationId}, {EfCoreVersion})""",
                cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);

        // SPEC-20260926-ingestion-jobs-test-deflake: WAL lets readers coexist
        // with the single writer — shrinks the SQLITE_BUSY window that can
        // strand a dequeued ingestion job when its "running" save races a
        // concurrent write. Persisted in the db file; harmless if it fails.
        // Performance pragmas (2026-10-03): synchronous=NORMAL is safe under
        // WAL and removes a fsync per commit; temp_store/cache_size/mmap_size
        // are per-connection hints applied to the migration connection —
        // pooled scoped contexts inherit journal_mode (persisted) but get
        // their own pragma state, so they are re-applied on each open by
        // SqlitePragmaInterceptor. optimize refreshes planner statistics.
        if (db.Database.IsSqlite())
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "PRAGMA journal_mode=WAL; PRAGMA optimize;", cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not enable SQLite WAL mode — continuing with the default journal");
            }
        }
    }

    private static async Task<bool> HasTableAsync(KnowledgeHubDbContext db, string table, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "name";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }
    }
}
