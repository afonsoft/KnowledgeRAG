using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// SqlDatabase connector (SPEC-20260927-restapi-sqldatabase-connectors RF-004/RF-005/RF-006):
/// runs a SELECT-only query against a sqlite or postgres database opened from
/// the encrypted store (<c>sql:{sourceId}</c>) and maps each row to a
/// <see cref="RawDocument"/>. Read-only is enforced three ways — the
/// <see cref="SqlQueryGuard"/> rejects write statements before any connection
/// opens, sqlite connections get <c>Mode=ReadOnly</c> and postgres queries run
/// inside a READ ONLY transaction that always rolls back.
/// </summary>
public sealed class SqlDatabaseConnector(
    IIntegrationSecretStore secrets,
    ILogger<SqlDatabaseConnector> logger) : ISourceConnector
{
    private const int MaxValueChars = 50 * 1024;
    private const string TruncatedMarker = " […truncated]";
    private const int ProviderErrorLimit = 300;
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(500);

    public SourceType Type => SourceType.SqlDatabase;

    /// <summary>Secret-store key holding the connection string.</summary>
    public static string SecretKey(Guid sourceId) => $"sql:{sourceId}";

    public async Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var provider = config.String("provider")?.ToLowerInvariant();
        if (provider is not ("sqlite" or "postgres"))
            throw new InvalidOperationException(
                $"SqlDatabase source '{source.Name}': provider inválido — use 'sqlite' ou 'postgres'");

        var query = config.String("query");
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException($"SqlDatabase source '{source.Name}' has no 'query'");

        var (ok, reason) = SqlQueryGuard.Validate(query);
        if (!ok)
            throw new InvalidOperationException(
                $"SqlDatabase source '{source.Name}': query rejeitada (read-only): {reason}");

        var stored = await secrets.GetAsync(SecretKey(source.Id), cancellationToken);
        if (stored is null)
            throw new InvalidOperationException(
                $"SqlDatabase source '{source.Name}': connection string não configurada — salve a source com a connection string (armazenada criptografada)");
        var connectionString = stored;

        var maxRows = config.Int("maxRows", 1000, 1, 10_000);
        var commandTimeout = config.Int("commandTimeoutSeconds", 30, 5, 300);
        var idColumns = config.StringArray("idColumn");
        var titleColumn = config.String("titleColumn");
        var contentColumns = config.StringArray("contentColumns");
        var urlColumn = config.String("urlColumn");

        try
        {
            await using var connection = CreateConnection(provider, connectionString);
            await connection.OpenAsync(cancellationToken);

            return await RunQueryAsync(
                connection,
                new QuerySpec(query, commandTimeout, maxRows, provider == "postgres"),
                new ColumnMapping(idColumns, titleColumn, contentColumns, urlColumn),
                source.Name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var sanitized = Sanitize(ex.Message, connectionString);
            throw new InvalidOperationException(
                $"SqlDatabase source '{source.Name}': {provider} connection/query failed: {sanitized}", ex);
        }
    }

    private static DbConnection CreateConnection(string provider, string connectionString) =>
        provider == "sqlite"
            ? new SqliteConnection(PrepareSqliteConnectionString(connectionString))
            : new NpgsqlConnection(connectionString);

    /// <summary>Query execution settings for <see cref="RunQueryAsync"/>.</summary>
    private sealed record QuerySpec(
        string Query, int CommandTimeout, int MaxRows, bool PostgresReadOnlyTransaction);

    /// <summary>Column-mapping configuration for <see cref="RunQueryAsync"/>.</summary>
    private sealed record ColumnMapping(
        string[] IdColumns, string? TitleColumn, string[] ContentColumns, string? UrlColumn);

    /// <summary>Runs the validated query and maps rows (RF-005). Postgres runs
    /// inside a READ ONLY transaction that always rolls back.</summary>
    private async Task<FetchResult> RunQueryAsync(
        DbConnection connection, QuerySpec spec, ColumnMapping mapping,
        string sourceName, CancellationToken ct)
    {
        var (query, commandTimeout, maxRows, postgresReadOnlyTransaction) = spec;
        var (idColumns, titleColumn, contentColumns, urlColumn) = mapping;
        var warnings = new List<string>();
        var warned = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<RawDocument>();
        var truncated = false;

        DbTransaction? transaction = null;
        try
        {
            if (postgresReadOnlyTransaction)
                transaction = await BeginReadOnlyTransactionAsync(connection, ct);

            await using var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = commandTimeout;
            if (transaction is not null)
                command.Transaction = transaction;

            await using var reader = await command.ExecuteReaderAsync(ct);
            var rowCtx = BuildRowContext(reader, mapping, warnings, warned);
            truncated = await ReadRowsAsync(reader, rowCtx, maxRows, documents, warnings, warned, ct);
        }
        finally
        {
            if (transaction is not null)
            {
                await using var _ = transaction;
                try { await transaction.RollbackAsync(ct); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
                { /* read-only tx — nothing to roll back anyway */ }
            }
        }

        if (truncated)
            logger.LogWarning("SqlDatabase source '{SourceName}' truncated at maxRows={MaxRows} rows", sourceName, maxRows);

        return new FetchResult(documents, warnings, Truncated: truncated);
    }

    /// <summary>Opens the Postgres READ ONLY transaction (always rolled back
    /// in the finally block — the query must never mutate).</summary>
    private static async Task<DbTransaction> BeginReadOnlyTransactionAsync(
        DbConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(ct);
        using var setReadOnly = connection.CreateCommand();
        setReadOnly.Transaction = transaction;
        setReadOnly.CommandText = "SET TRANSACTION READ ONLY";
        await setReadOnly.ExecuteNonQueryAsync(ct);
        return transaction;
    }

    /// <summary>Column ordinals for <see cref="ReadRowsAsync"/>; absent
    /// content columns fall back to every non-id/non-title column.</summary>
    private sealed record RowContext(
        string[] ColumnNames, int[] IdOrdinals, int TitleOrdinal,
        int UrlOrdinal, int[] ContentOrdinals);

    /// <summary>Resolves configured columns to reader ordinals, warning on
    /// missing ones (SPEC §6).</summary>
    private RowContext BuildRowContext(
        DbDataReader reader, ColumnMapping mapping,
        List<string> warnings, HashSet<string> warned)
    {
        var columnNames = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columnNames[i] = reader.GetName(i);

        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            ordinals.TryAdd(columnNames[i], i);

        int OrdinalOrWarn(string column)
        {
            if (ordinals.TryGetValue(column, out var ordinal))
                return ordinal;
            WarnOnce(warnings, warned, $"column '{column}' not found in result set");
            return -1;
        }

        var idOrdinals = mapping.IdColumns.Select(OrdinalOrWarn).Where(o => o >= 0).ToArray();
        var titleOrdinal = mapping.TitleColumn is null ? -1 : OrdinalOrWarn(mapping.TitleColumn);
        var urlOrdinal = mapping.UrlColumn is null ? -1 : OrdinalOrWarn(mapping.UrlColumn);
        var contentOrdinals = mapping.ContentColumns.Length > 0
            ? mapping.ContentColumns.Select(OrdinalOrWarn).Where(o => o >= 0).ToArray()
            : Enumerable.Range(0, columnNames.Length)
                .Where(i => !idOrdinals.Contains(i) && i != titleOrdinal)
                .ToArray();

        return new RowContext(columnNames, idOrdinals, titleOrdinal, urlOrdinal, contentOrdinals);
    }

    /// <summary>Reads rows up to maxRows; returns whether truncation occurred.</summary>
    private async Task<bool> ReadRowsAsync(
        DbDataReader reader, RowContext ctx, int maxRows,
        List<RawDocument> documents, List<string> warnings,
        HashSet<string> warned, CancellationToken ct)
    {
        var rowNumber = 0;
        while (await reader.ReadAsync(ct))
        {
            rowNumber++;
            if (documents.Count >= maxRows)
            {
                WarnOnce(warnings, warned, $"truncated at maxRows ({maxRows}) — query returned more rows");
                return true;
            }

            documents.Add(MapRow(reader, ctx, rowNumber));
        }
        return false;
    }

    /// <summary>Maps one row: title, content lines (NULL columns omitted per
    /// SPEC §6) and a URI from id columns → url column → row hash.</summary>
    private static RawDocument MapRow(DbDataReader reader, RowContext ctx, int rowNumber)
    {
        string? Value(int ordinal) => ReadValue(reader, ordinal);

        var title = Value(ctx.TitleOrdinal) ?? $"row {rowNumber}";

        var lines = new List<string>();
        var rowValues = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = Value(i);
            if (value is null)
                continue; // edge case (SPEC §6): NULL columns are omitted
            rowValues.Add(value);
        }
        foreach (var ordinal in ctx.ContentOrdinals)
        {
            var value = Value(ordinal);
            if (value is null)
                continue;
            lines.Add($"{ctx.ColumnNames[ordinal]}: {Truncate(value)}");
        }

        var idValues = ctx.IdOrdinals.Select(Value).Where(v => v is { Length: > 0 }).ToArray();
        string uri;
        if (idValues.Length > 0)
            uri = $"sql:{string.Join(":", idValues)}";
        else if (Value(ctx.UrlOrdinal) is { Length: > 0 } url)
            uri = url;
        else
            uri = $"sql:{Sha256Hex(string.Join("|", rowValues))}";

        return new RawDocument(uri, title, string.Join("\n", lines));
    }

    /// <summary>Null-safe cell reader (invariant culture for non-strings).</summary>
    private static string? ReadValue(DbDataReader reader, int ordinal) =>
        ordinal >= 0 && !reader.IsDBNull(ordinal)
            ? Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
            : null;

    /// <summary>Forces <c>Mode=ReadOnly</c> when the caller did not specify a
    /// mode and resolves relative <c>Data Source</c> paths against the app
    /// base directory (DatabasePath-style portability).</summary>
    public static string PrepareSqliteConnectionString(string raw)
    {
        var builder = new SqliteConnectionStringBuilder(raw);
        if (!string.IsNullOrEmpty(builder.DataSource) && !Path.IsPathRooted(builder.DataSource))
            builder.DataSource = Path.GetFullPath(builder.DataSource, AppContext.BaseDirectory);

        bool hasExplicitMode;
        try
        {
            hasExplicitMode = System.Text.RegularExpressions.Regex.IsMatch(
                raw, @"(^|;)\s*Mode\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                RegexMatchTimeout);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            hasExplicitMode = false;
        }
        if (!hasExplicitMode)
            builder.Mode = SqliteOpenMode.ReadOnly;

        return builder.ToString();
    }

    private static string Truncate(string value) =>
        value.Length > MaxValueChars ? value[..MaxValueChars] + TruncatedMarker : value;

    private static string Sanitize(string message, string connectionString)
    {
        var withoutSecret = message.Replace(connectionString, "***", StringComparison.OrdinalIgnoreCase);
        return withoutSecret.Length <= ProviderErrorLimit
            ? withoutSecret
            : withoutSecret[..ProviderErrorLimit] + "…";
    }

    private static void WarnOnce(List<string> warnings, HashSet<string> warned, string message)
    {
        if (warned.Add(message))
            warnings.Add(message);
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
