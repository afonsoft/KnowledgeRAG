using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260926-unified-database-provider — the Postgres catalog path verified
/// end-to-end against a real pgvector container (shared fixture). Skips silently
/// when Docker is unavailable, same convention as PostgresVectorStoreLiveTests.
/// </summary>
[Collection(nameof(PgVectorCollection))]
public sealed class UnifiedDatabaseProviderTests
{
    private readonly PgVectorFixture _fixture;

    public UnifiedDatabaseProviderTests(PgVectorFixture fixture) => _fixture = fixture;

    private PostgresKnowledgeHubDbContext NewPgContext() =>
        new(new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options);

    // RF-002: the Postgres migration set applies cleanly on an empty database —
    // catalog tables + tsvector generated column + GIN index.
    [Fact]
    public async Task PostgresMigrations_CreateCatalogSchema_WithSearchVector()
    {
        if (!_fixture.Available) return;

        var schema = $"u{Guid.NewGuid():N}";
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString!)
        { SearchPath = schema };
        await using (var create = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
        {
            await create.OpenAsync();
            await using var cmd = create.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA {schema}";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = new PostgresKnowledgeHubDbContext(
            new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>().UseNpgsql(csb.ConnectionString).Options);
        await db.Database.MigrateAsync();

        await using var conn = new Npgsql.NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();
        await using var check = conn.CreateCommand();
        check.CommandText = """
            SELECT
                (SELECT count(*) FROM information_schema.tables
                 WHERE table_schema = current_schema() AND table_name IN
                    ('Sources','Documents','Chunks','Users','IngestionJobs')),
                (SELECT count(*) FROM information_schema.columns
                 WHERE table_schema = current_schema() AND table_name = 'Chunks'
                    AND column_name = 'search_vector'),
                (SELECT count(*) FROM pg_indexes
                 WHERE schemaname = current_schema() AND indexname = 'IX_Chunks_search_vector')
            """;
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }

    // RF-005: one-shot sqlite→postgres copy preserves GUID keys so existing
    // kh_embeddings rows stay joined to their chunks.
    [Fact]
    public async Task SqliteToPostgresMigrator_CopiesCatalog_PreservingIds()
    {
        if (!_fixture.Available) return;

        var schema = $"u{Guid.NewGuid():N}";
        await using (var create = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
        {
            await create.OpenAsync();
            await using var cmd = create.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA {schema}";
            await cmd.ExecuteNonQueryAsync();
        }
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString!)
        { SearchPath = schema };

        // Source sqlite catalog with a source → document → chunk chain.
        var sqlitePath = Path.Join(Path.GetTempPath(), $"kh-mig-{Guid.NewGuid():N}.db");
        try
        {
            await using (var sqlite = new KnowledgeHubDbContext(
                new DbContextOptionsBuilder<KnowledgeHubDbContext>()
                    .UseSqlite($"Data Source={sqlitePath}").Options))
            {
                await sqlite.Database.EnsureCreatedAsync();
                var src = new KnowledgeSource { Name = $"mig-{Guid.NewGuid():N}", SourceType = SourceType.DocumentFile };
                sqlite.Sources.Add(src);
                var doc = new KnowledgeDocument
                {
                    KnowledgeSourceId = src.Id,
                    Title = "a.txt",
                    UriReference = "/a.txt",
                    ContentHash = "h"
                };
                sqlite.Documents.Add(doc);
                sqlite.Chunks.Add(new DocumentChunk
                {
                    KnowledgeDocumentId = doc.Id,
                    TextContent = "chunk text",
                    Embedding = [1, 0, 0, 0]
                });
                await sqlite.SaveChangesAsync();
            }

            // Orphan row — real catalogs were written with PRAGMA
            // foreign_keys=OFF historically, so children can reference deleted
            // parents; Postgres rejects them (23503, hit in deploy). The
            // migrator must skip them, not crash. Seed via raw ADO since EF
            // enforces FKs on its own connections.
            await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={sqlitePath}"))
            {
                await raw.OpenAsync();
                await using var pragma = raw.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys=OFF";
                await pragma.ExecuteNonQueryAsync();
                await using var ins = raw.CreateCommand();
                ins.CommandText = """
                    INSERT INTO ApiKeyUsageEvents (Id, ApiKeyId, Timestamp, HttpMethod, Path, StatusCode, DurationMs)
                    VALUES ($id, $fk, $ts, 'GET', '/api/x', 200, 1.0)
                    """;
                ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                ins.Parameters.AddWithValue("$fk", Guid.NewGuid().ToString());
                ins.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
                await ins.ExecuteNonQueryAsync();
            }

            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = sqlitePath
                })
                .Build();

            await using var pg = new PostgresKnowledgeHubDbContext(
                new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                    .UseNpgsql(csb.ConnectionString).Options);
            await pg.Database.MigrateAsync();
            await SqliteToPostgresMigrator.RunAsync(pg, cfg, NullLogger.Instance);

            var pgSource = await pg.Sources.SingleAsync();
            var pgChunk = await pg.Chunks.SingleAsync();
            Assert.StartsWith("mig-", pgSource.Name);
            Assert.Equal("chunk text", pgChunk.TextContent);
            Assert.NotNull(pgChunk.Embedding);
            Assert.True(File.Exists(sqlitePath + ".migrated"));

            // Idempotent: second run sees populated catalog and skips.
            await SqliteToPostgresMigrator.RunAsync(pg, cfg, NullLogger.Instance);
            Assert.Equal(1, await pg.Sources.CountAsync());
        }
        finally
        {
            try { File.Delete(sqlitePath); File.Delete(sqlitePath + ".migrated"); }
            catch (Exception ioEx) { _ = ioEx.Message; /* best-effort temp cleanup */ }
        }
    }

    // RF-003: the tsvector lexical path returns ranked hits on a postgres
    // catalog — exercises the generated column + websearch_to_tsquery query.
    [Fact]
    public async Task LexicalSearch_Postgres_RanksViaTsvector()
    {
        if (!_fixture.Available) return;

        var schema = $"u{Guid.NewGuid():N}";
        await using (var create = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
        {
            await create.OpenAsync();
            await using var cmd = create.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA {schema}";
            await cmd.ExecuteNonQueryAsync();
        }
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString!)
        { SearchPath = schema };

        await using var pg = new PostgresKnowledgeHubDbContext(
            new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                .UseNpgsql(csb.ConnectionString).Options);
        await pg.Database.MigrateAsync();

        var src = new KnowledgeSource { Name = $"lex-{Guid.NewGuid():N}", SourceType = SourceType.DocumentFile };
        var doc = new KnowledgeDocument
        {
            KnowledgeSourceId = src.Id,
            Title = "t",
            UriReference = "/t"
        };
        var chunk = new DocumentChunk
        {
            KnowledgeDocumentId = doc.Id,
            TextContent = "postgresql unifies catalog and vector storage"
        };
        pg.Sources.Add(src);
        pg.Documents.Add(doc);
        pg.Chunks.Add(chunk);
        await pg.SaveChangesAsync();

        var lexical = new KnowledgeHub.Server.Search.LexicalSearchService(
            pg, new ConfigurationBuilder().Build(),
            new CatalogDatabase(CatalogProvider.Postgres, csb.ConnectionString),
            NullLogger<KnowledgeHub.Server.Search.LexicalSearchService>.Instance);

        var hits = await lexical.SearchAsync("unifies vector", 5, null);
        Assert.Single(hits);
        Assert.Equal(chunk.Id, hits[0].ChunkId);
    }

    // RF-002 regression: the DbContextPool requires a ctor taking
    // DbContextOptions<TImpl> — a base-typed options ctor crashes the host at
    // first resolution (hit once in deploy: "cannot be pooled").
    [Fact]
    public async Task PostgresContext_PooledRegistration_ResolvesAndMigrates()
    {
        if (!_fixture.Available) return;

        var schema = $"u{Guid.NewGuid():N}";
        await using (var create = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
        {
            await create.OpenAsync();
            await using var cmd = create.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA {schema}";
            await cmd.ExecuteNonQueryAsync();
        }
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString!)
        { SearchPath = schema };

        var services = new ServiceCollection();
        services.AddDbContextPool<KnowledgeHubDbContext, PostgresKnowledgeHubDbContext>(
            o => o.UseNpgsql(csb.ConnectionString)
                  .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>());
        await using var sp = services.BuildServiceProvider();

        await using var db = sp.GetRequiredService<KnowledgeHubDbContext>();
        Assert.IsType<PostgresKnowledgeHubDbContext>(db);
        await db.Database.MigrateAsync();
        Assert.True(await db.Sources.CountAsync() >= 0);
    }

    // RF-001: provider resolution — auto falls back to sqlite without config,
    // explicit postgres without connstring is a config error.
    [Fact]
    public void Resolve_AutoWithoutPostgres_FallsBackToSqlite()
    {
        var cfg = new ConfigurationBuilder().Build();
        var resolved = CatalogDatabase.Resolve(cfg);
        Assert.Equal(CatalogProvider.Sqlite, resolved.Provider);
    }

    [Fact]
    public void Resolve_ExplicitPostgresWithoutConnString_Throws()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["Database:ConnectionString"] = "",
            })
            .Build();
        // No POSTGRES_* env in the test process → connstring unresolved → error.
        if (Environment.GetEnvironmentVariable("POSTGRES_HOST") is not null)
            return; // host env configured — skip the negative assertion
        Assert.Throws<InvalidOperationException>(() => CatalogDatabase.Resolve(cfg));
    }

    // RF-001: auto with an unreachable Postgres falls back to sqlite with reason.
    [Fact]
    public void Resolve_AutoWithUnreachablePostgres_FallsBackWithReason()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "auto",
                // unrachable-by-construction port
                ["Database:ConnectionString"] =
                    "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1",
            })
            .Build();
        var resolved = CatalogDatabase.Resolve(cfg);
        Assert.Equal(CatalogProvider.Sqlite, resolved.Provider);
        Assert.NotNull(resolved.FallbackReason);
    }
}
