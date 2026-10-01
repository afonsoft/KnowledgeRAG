using System.Data;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20260926-settings-tabs-database-metrics RF-002: builds the
/// <see cref="DatabaseStatsDto"/> for GET /api/settings/database — EF provider,
/// SQLite file/WAL sizes, PRAGMA stats, per-entity row counts (table names come
/// from the EF model, never from user input), migrations and vector store info.
/// Every section fails soft — a broken stat degrades to null, never a 500.
/// </summary>
public static class DatabaseStatsBuilder
{
    public static async Task<DatabaseStatsDto> BuildAsync(
        KnowledgeHubDbContext db, IConfiguration cfg,
        IVectorStore vectors, CancellationToken ct)
    {
        var dto = new DatabaseStatsDto
        {
            Provider = db.Database.ProviderName ?? "",
        };

        try { dto.VectorStore = await VectorStoreDiagnostics.BuildAsync(vectors, cfg, db, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* vector diagnostics optional */ }

        try
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct))
                .OrderBy(m => m, StringComparer.Ordinal).ToList();
            dto.MigrationsApplied = applied.Count;
            dto.LastMigration = applied.LastOrDefault();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* migrations unavailable */ }

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        var tables = new List<TableCountDto>();
        foreach (var et in db.Model.GetEntityTypes()
                     .Select(e => e.GetTableName())
                     .Where(n => !string.IsNullOrEmpty(n))
                     .OrderBy(n => n))
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{et}\""; // NOSONAR S2077 — et é nome de tabela EF-mapped (metadado interno, não input)
                var v = await cmd.ExecuteScalarAsync(ct);
                tables.Add(new TableCountDto { Name = et!, RowCount = Convert.ToInt64(v) });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* individual table count optional */ }
        }
        dto.Tables = tables;

        if (dto.Provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            EnrichSqlite(dto, conn, cfg, ct);
        else
            // Non-SQLite providers (e.g. Npgsql): report host[:port]/database
            // instead of a file path — never the connection string (has creds).
            dto.DataSource = $"{conn.DataSource}/{conn.Database}";

        return dto;
    }

    private static void EnrichSqlite(
        DatabaseStatsDto dto, IDbConnection conn, IConfiguration cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var path = DatabasePath.Resolve(cfg);
            dto.DataSource = path;
            var fi = new FileInfo(path);
            if (fi.Exists)
                dto.FileSizeBytes = fi.Length;
            var wal = new FileInfo(path + "-wal");
            if (wal.Exists)
                dto.WalSizeBytes = wal.Length;
        }
        catch { /* file info optional */ }

        dto.PageCount = Pragma(conn, "page_count");
        dto.PageSizeBytes = Pragma(conn, "page_size");
        dto.FreelistCount = Pragma(conn, "freelist_count");
        dto.CacheSizePages = Pragma(conn, "cache_size");
    }

    private static long? Pragma(IDbConnection conn, string name)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {name}"; // NOSONAR S2077 — name é identificador pragma de lista interna fixa
            var v = cmd.ExecuteScalar();
            return v is null ? null : Convert.ToInt64(v);
        }
        catch { return null; }
    }
}
