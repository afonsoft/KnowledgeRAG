using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-restapi-sqldatabase-connectors RF-004/RF-005: SELECT-only
// guard, sqlite row mapping, maxRows truncation, NULL omission, 50 KB value
// truncation, secret resolution and read-only connection string preparation.
public class SqlDatabaseConnectorTests : IDisposable
{
    private readonly string _dbPath;

    public SqlDatabaseConnectorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kh-sqltest-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        Exec(connection, "CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT, body TEXT, url TEXT)");
        Exec(connection, "INSERT INTO notes (id, title, body, url) VALUES (1, 'Alpha', 'alpha body', 'https://x/1')");
        Exec(connection, "INSERT INTO notes (id, title, body) VALUES (2, 'Beta', NULL)");
        Exec(connection, "CREATE TABLE wide (a TEXT)");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private sealed class FakeSecrets(string? connectionString) : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) => Task.FromResult(connectionString);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static KnowledgeSource Source(object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "sql-src",
        SourceType = SourceType.SqlDatabase,
        ConfigurationJson = System.Text.Json.JsonSerializer.Serialize(configuration)
    };

    private static SqlDatabaseConnector Sut(string? connectionString) =>
        new(new FakeSecrets(connectionString), NullLogger<SqlDatabaseConnector>.Instance);

    private static object Cfg(string query, string? idColumn = null, string? titleColumn = null,
        string[]? contentColumns = null, string? urlColumn = null, int? maxRows = null) => new
        {
            provider = "sqlite",
            query,
            idColumn,
            titleColumn,
            contentColumns,
            urlColumn,
            maxRows
        };

    [Fact]
    public async Task SqliteRows_BecomeDocuments()
    {
        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT id, title, body FROM notes ORDER BY id",
                idColumn: "id", titleColumn: "title")), CancellationToken.None);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal("sql:1", result.Documents[0].UriReference);
        Assert.Equal("Alpha", result.Documents[0].Title);
        Assert.Contains("alpha body", result.Documents[0].TextContent);
        Assert.True(result.Truncated is false);
    }

    [Fact]
    public async Task NullColumns_OmittedFromContent()
    {
        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT id, title, body FROM notes WHERE id = 2", idColumn: "id")),
            CancellationToken.None);

        var content = Assert.Single(result.Documents).TextContent;
        Assert.Contains("title: Beta", content);
        Assert.DoesNotContain("body:", content);
    }

    [Fact]
    public async Task NoContentColumns_UsesAllColumnsExceptId()
    {
        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT id, title, body, url FROM notes WHERE id = 1", idColumn: "id")),
            CancellationToken.None);

        var content = Assert.Single(result.Documents).TextContent;
        Assert.Contains("title: Alpha", content);
        Assert.Contains("alpha body", content);
        Assert.Contains("url: https://x/1", content);
        Assert.DoesNotContain("id: 1", content);
    }

    [Fact]
    public async Task MultiColumnId_JoinedWithColon()
    {
        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT id, title FROM notes WHERE id = 1", idColumn: "id,title")),
            CancellationToken.None);

        Assert.Equal("sql:1:Alpha", Assert.Single(result.Documents).UriReference);
    }

    [Fact]
    public async Task UrlColumn_UsedWhenNoIdColumn()
    {
        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT title, url FROM notes WHERE id = 1", urlColumn: "url")),
            CancellationToken.None);

        Assert.Equal("https://x/1", Assert.Single(result.Documents).UriReference);
    }

    [Fact]
    public async Task MissingColumn_WarnsOnce()
    {
        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT id, title, body FROM notes", idColumn: "id", contentColumns: new[] { "title", "nope" })),
            CancellationToken.None);

        Assert.Equal(2, result.Documents.Count);
        Assert.Single(result.Warnings);
        Assert.Contains("nope", result.Warnings[0]);
    }

    [Fact]
    public async Task MaxRows_TruncatesWithWarningAndFlag()
    {
        for (var i = 10; i < 15; i++)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            Exec(connection, $"INSERT INTO notes (id, title, body) VALUES ({i}, 'N{i}', 'b{i}')");
        }

        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT id, title FROM notes", idColumn: "id", maxRows: 3)), CancellationToken.None);

        Assert.Equal(3, result.Documents.Count);
        Assert.True(result.Truncated);
        Assert.Contains(result.Warnings, w => w.Contains("truncated at maxRows"));
    }

    [Fact]
    public async Task LargeValue_TruncatedAt50KbWithMarker()
    {
        var big = new string('x', 51 * 1024);
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            Exec(connection, $"INSERT INTO wide (a) VALUES ('{big}')");
        }

        var result = await Sut($"Data Source={_dbPath}").FetchAsync(
            Source(Cfg("SELECT a FROM wide")), CancellationToken.None);

        var content = Assert.Single(result.Documents).TextContent;
        Assert.True(content.Length < 51 * 1024, $"expected truncation, got {content.Length}");
        Assert.Contains("[…truncated]", content);
    }

    [Fact]
    public async Task WriteQuery_RejectedBeforeConnection()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut($"Data Source={_dbPath}").FetchAsync(
                Source(Cfg("DELETE FROM notes")), CancellationToken.None));

        Assert.Contains("SELECT", ex.Message);
    }

    [Fact]
    public async Task MissingQuery_ThrowsClearError()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut($"Data Source={_dbPath}").FetchAsync(
                Source(new { provider = "sqlite" }), CancellationToken.None));

        Assert.Contains("query", ex.Message);
    }

    [Fact]
    public async Task InvalidProvider_ThrowsClearError()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut("Host=x").FetchAsync(
                Source(new { provider = "mysql", query = "SELECT 1" }), CancellationToken.None));

        Assert.Contains("provider", ex.Message);
        Assert.Contains("sqlite", ex.Message);
        Assert.Contains("postgres", ex.Message);
    }

    [Fact]
    public async Task MissingSecret_ThrowsWithoutEchoingConfig()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(null).FetchAsync(
                Source(Cfg("SELECT id FROM notes", idColumn: "id")), CancellationToken.None));

        Assert.Contains("connection string", ex.Message);
    }

    [Fact]
    public async Task ConnectionFailure_SanitizedMessage_NoConnStringEcho()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"kh-missing-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={missing};Mode=ReadOnly";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(cs).FetchAsync(
                Source(Cfg("SELECT id FROM notes", idColumn: "id")), CancellationToken.None));

        Assert.DoesNotContain("Data Source", ex.Message);
        Assert.Contains("sql-src", ex.Message);
    }

    [Fact]
    public async Task HasKeyWithoutSecret_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(null).FetchAsync(
                Source(new { provider = "sqlite", query = "SELECT 1", hasKey = true }),
                CancellationToken.None));

        Assert.Contains("salve a source", ex.Message);
    }

    [Fact]
    public void PrepareSqliteConnectionString_AppendsReadOnlyAndResolvesRelativePaths()
    {
        var absolute = SqlDatabaseConnector.PrepareSqliteConnectionString($"Data Source={_dbPath}");
        Assert.Contains("Mode=ReadOnly", absolute, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_dbPath, absolute, StringComparison.OrdinalIgnoreCase);

        var explicitMode = SqlDatabaseConnector.PrepareSqliteConnectionString(
            $"Data Source={_dbPath};Mode=Memory");
        Assert.DoesNotContain("Mode=ReadOnly", explicitMode, StringComparison.OrdinalIgnoreCase);

        var relative = SqlDatabaseConnector.PrepareSqliteConnectionString("Data Source=notes.db");
        var resolved = Path.Combine(AppContext.BaseDirectory, "notes.db");
        Assert.Contains(resolved, relative, StringComparison.OrdinalIgnoreCase);
    }
}
