using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KnowledgeHub.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260930-migration-populated-db-tests (issue #430, epic #428) —
/// regression coverage for the 2026-09-30 production incident: the
/// TemporalEpisodicGraph migration added `KgNodes.Labels` NOT NULL without a
/// default and crashed on populated databases (23502 on Postgres). Every test
/// here seeds rows in a schema <em>before</em> that migration, then runs the
/// real migrator to the latest version.
/// Convention: `AddColumn nullable:false` on an existing table requires a
/// `defaultValue`/`defaultValueSql` or a nullable→backfill→NOT NULL sequence.
/// </summary>
public sealed class MigrationPopulatedDbTests
{
    /// <summary>The migration that first added the temporal/episodic columns.</summary>
    private const string BoundaryMigrationFragment = "TemporalEpisodicGraph";

    private static string MigrationBefore(DbContext db, string fragment)
    {
        var migrations = db.Database.GetMigrations().ToList();
        var idx = migrations.FindIndex(m => m.Contains(fragment, StringComparison.Ordinal));
        Assert.True(idx > 0, $"migration containing '{fragment}' not found or is first");
        return migrations[idx - 1];
    }

    private static async Task SeedKgNodePreBoundaryAsync(SqliteConnection conn)
    {
        // Old-schema row: only the columns created by AddKnowledgeGraph
        // (all NOT NULL). Columns added later must tolerate the missing values.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "KgNodes" ("Id", "Name", "NormalizedName", "Type", "FirstSeenAt")
            VALUES ($id, 'Migrated Node', 'migrated node', 'concept', $seen)
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    // RF-001 — SQLite, runs in the default CI gate.
    [Fact]
    public async Task Sqlite_Migration_OverPopulatedKgNodes_PreservesRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kh-mig-{Guid.NewGuid():N}.db");
        try
        {
            var boundary = (string?)null;
            await using (var db = new KnowledgeHubDbContext(
                new DbContextOptionsBuilder<KnowledgeHubDbContext>()
                    .UseSqlite($"Data Source={path}").Options))
            {
                boundary = MigrationBefore(db, BoundaryMigrationFragment);
                await db.GetService<IMigrator>().MigrateAsync(boundary);
            }

            await using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                await SeedKgNodePreBoundaryAsync(conn);
            }

            await using (var db = new KnowledgeHubDbContext(
                new DbContextOptionsBuilder<KnowledgeHubDbContext>()
                    .UseSqlite($"Data Source={path}").Options))
            {
                await db.Database.MigrateAsync();
            }

            await using var check = new SqliteConnection($"Data Source={path}");
            await check.OpenAsync();
            await using var q = check.CreateCommand();
            q.CommandText = """SELECT "Labels", "ObservedAt", "ValidFrom" FROM "KgNodes" LIMIT 1""";
            await using var reader = await q.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "seeded KgNodes row lost during migration");
            Assert.Equal("[]", reader.GetString(0));
            Assert.False(await reader.IsDBNullAsync(1), "ObservedAt must be backfilled non-NULL");
            Assert.False(await reader.IsDBNullAsync(2), "ValidFrom must be backfilled non-NULL");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // RF-002 — Postgres, skips when the live fixture is unavailable
    // (same convention as PostgresVectorStoreLiveTests / UnifiedDatabaseProviderTests).
    [Collection(nameof(PgVectorCollection))]
    public sealed class Postgres
    {
        private readonly PgVectorFixture _fixture;

        public Postgres(PgVectorFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task Postgres_Migration_OverPopulatedKgNodes_PreservesRows()
        {
            if (!_fixture.Available) return;

            var schema = $"mig{Guid.NewGuid():N}";
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString!)
            { SearchPath = schema };
            await using (var create = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
            {
                await create.OpenAsync();
                await using var cmd = create.CreateCommand();
                cmd.CommandText = $"CREATE SCHEMA {schema}";
                await cmd.ExecuteNonQueryAsync();
            }

            DbContextOptions<PostgresKnowledgeHubDbContext> Opts() =>
                new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                    .UseNpgsql(csb.ConnectionString).Options;

            var boundary = (string?)null;
            await using (var db = new PostgresKnowledgeHubDbContext(Opts()))
            {
                boundary = MigrationBefore(db, BoundaryMigrationFragment);
                await db.GetService<IMigrator>().MigrateAsync(boundary);
            }

            await using (var conn = new Npgsql.NpgsqlConnection(csb.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO "KgNodes" ("Id", "Name", "NormalizedName", "Type", "FirstSeenAt")
                    VALUES (@id, 'Migrated Node', 'migrated node', 'concept', now())
                    """;
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var db = new PostgresKnowledgeHubDbContext(Opts()))
            {
                await db.Database.MigrateAsync();
            }

            await using var check = new Npgsql.NpgsqlConnection(csb.ConnectionString);
            await check.OpenAsync();
            await using var q = check.CreateCommand();
            q.CommandText = """SELECT "Labels", "ObservedAt", "ValidFrom" FROM "KgNodes" LIMIT 1""";
            await using var reader = await q.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "seeded KgNodes row lost during migration");
            Assert.Equal(Array.Empty<string>(), (string[])reader.GetValue(0));
            Assert.False(await reader.IsDBNullAsync(1), "ObservedAt must be backfilled non-NULL");
            Assert.False(await reader.IsDBNullAsync(2), "ValidFrom must be backfilled non-NULL");
        }
    }
}
