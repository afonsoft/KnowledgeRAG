using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260927-restapi-sqldatabase-connectors ACs: validation 400s
// (provider/query/endpoint/headers), secret round-trip (create→hasKey, update
// without resending keeps the stored secret), end-to-end sync of both
// connectors and dedup on re-sync.
public class RestApiSqlSourcesTests : IClassFixture<RestApiSqlSourcesTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"kh-restapisql-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private sealed class FakeRestApi : HttpMessageHandler
    {
        public const string Url = "https://restapi.test/data";
        public const string Marker = "RESTSQLTOKEN77";
        public int Hits { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hits++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"items":[{"id":"a","title":"Alpha","body":"{{Marker}}"}]}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private readonly HttpClient _client;
    private readonly string _sqlitePath;
    private readonly FakeRestApi _fake = new();

    public RestApiSqlSourcesTests(Fixture fixture)
    {
        // The "restapi" named client is overridden with a deterministic fake;
        // the factory returned by WithWebHostBuilder is the one to login on.
        var restFactory = fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddHttpClient("restapi")
                .ConfigurePrimaryHttpMessageHandler(() => _fake);
        }));
        _client = TestAuth.Login(restFactory);
        _sqlitePath = Path.Combine(Path.GetTempPath(), $"kh-sqlsrc-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={_sqlitePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT, body TEXT);" +
                "INSERT INTO notes (title, body) VALUES ('Alpha', 'SQLTOKEN77 body')";
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        try { File.Delete(_sqlitePath); } catch { /* best effort */ }
    }

    private async Task<HttpResponseMessage> PostSourceAsync(object configuration, string type)
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"src-{Guid.NewGuid():N}",
            type,
            configuration,
            isActive = true
        });
        return response;
    }

    private async Task<KnowledgeSourceDto> CreateSourceAsync(object configuration, string type)
    {
        var response = await PostSourceAsync(configuration, type);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
    }

    private async Task<SyncResultDto> SyncAsync(Guid id) =>
        (await (await _client.PostAsync($"/api/sources/{id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>())!;

    [Fact]
    public async Task SqlDatabase_InvalidProvider_Returns400()
    {
        var response = await PostSourceAsync(
            new { provider = "mysql", query = "SELECT 1", connectionString = "Host=x" }, "SqlDatabase");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("provider", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SqlDatabase_MissingProvider_Returns400()
    {
        var response = await PostSourceAsync(
            new { query = "SELECT 1", connectionString = "Data Source=x.db" }, "SqlDatabase");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SqlDatabase_WriteQuery_Returns400()
    {
        var response = await PostSourceAsync(
            new { provider = "sqlite", query = "DELETE FROM x", connectionString = $"Data Source={_sqlitePath}" },
            "SqlDatabase");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("read-only", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SqlDatabase_MultiStatementQuery_Returns400()
    {
        var response = await PostSourceAsync(
            new { provider = "sqlite", query = "SELECT 1; DROP TABLE x", connectionString = $"Data Source={_sqlitePath}" },
            "SqlDatabase");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RestApi_InvalidEndpoint_Returns400()
    {
        var response = await PostSourceAsync(new { endpoint = "not-a-url" }, "RestApi");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RestApi_InvalidHeaders_Returns400()
    {
        var response = await PostSourceAsync(
            new { endpoint = FakeRestApi.Url, headers = "not json {" }, "RestApi");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("headers", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RestApi_HasKeyWithoutSecret_Returns400()
    {
        var response = await PostSourceAsync(
            new { endpoint = FakeRestApi.Url, hasKey = true }, "RestApi");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SqlDatabase_SecretRoundTrip_KeepWithoutResend()
    {
        var source = await CreateSourceAsync(new
        {
            provider = "sqlite",
            query = "SELECT id, title, body FROM notes",
            idColumn = "id",
            titleColumn = "title",
            connectionString = $"Data Source={_sqlitePath}"
        }, "SqlDatabase");

        Assert.True(source.Configuration!["hasKey"]!.GetValue<bool>());
        Assert.Null(source.Configuration["connectionString"]);

        // Update without resending the connection string keeps the stored secret.
        var kept = await _client.PutAsJsonAsync($"/api/sources/{source.Id}", new
        {
            name = source.Name,
            configuration = new { provider = "sqlite", query = "SELECT id, title, body FROM notes", hasKey = true },
            isActive = true
        });
        kept.EnsureSuccessStatusCode();
        var keptDto = await kept.Content.ReadFromJsonAsync<KnowledgeSourceDto>();
        Assert.True(keptDto!.Configuration!["hasKey"]!.GetValue<bool>());

        // Clearing the connection string removes the secret → the next update
        // without it is rejected (the source could never sync).
        var cleared = await _client.PutAsJsonAsync($"/api/sources/{source.Id}", new
        {
            name = source.Name,
            configuration = new
            {
                provider = "sqlite",
                query = "SELECT id, title, body FROM notes",
                connectionString = ""
            },
            isActive = true
        });
        cleared.EnsureSuccessStatusCode();
        var clearedDto = await cleared.Content.ReadFromJsonAsync<KnowledgeSourceDto>();
        Assert.False(clearedDto!.Configuration!["hasKey"]!.GetValue<bool>());

        var stale = await _client.PutAsJsonAsync($"/api/sources/{source.Id}", new
        {
            name = source.Name,
            configuration = new { provider = "sqlite", query = "SELECT id, title, body FROM notes", hasKey = true },
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
    }

    [Fact]
    public async Task SqlDatabase_Sync_IndexesRows_SearchFindsToken()
    {
        var source = await CreateSourceAsync(new
        {
            provider = "sqlite",
            query = "SELECT id, title, body FROM notes",
            idColumn = "id",
            titleColumn = "title",
            connectionString = $"Data Source={_sqlitePath}"
        }, "SqlDatabase");

        var result = await SyncAsync(source.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.DocumentsProcessed);

        var docs = await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>(
            $"/api/sources/{source.Id}/documents");
        Assert.Single(docs!);
        Assert.Equal("Alpha", docs![0].Title);

        var search = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=SQLTOKEN77&mode=lexical");
        Assert.NotEmpty(search!.Results);

        // CA-009: re-sync without changes dedups by content hash.
        var resync = await SyncAsync(source.Id);
        Assert.Equal("completed", resync.Status);
        Assert.Equal(0, resync.DocumentsProcessed);
        Assert.Equal(1, resync.DocumentsSkipped);
    }

    [Fact]
    public async Task RestApi_Sync_IndexesItems_SearchFindsToken()
    {
        var source = await CreateSourceAsync(new
        {
            endpoint = FakeRestApi.Url,
            itemsPath = "items",
            idField = "id",
            titleField = "title"
        }, "RestApi");

        var result = await SyncAsync(source.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.DocumentsProcessed);

        var docs = await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>(
            $"/api/sources/{source.Id}/documents");
        Assert.Single(docs!);
        Assert.Equal("rest:a", docs![0].UriReference);

        var search = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=RESTSQLTOKEN77&mode=lexical");
        Assert.NotEmpty(search!.Results);
    }

    [Fact]
    public async Task RestApi_HeadersSecret_AppliedOnSync_NeverEchoed()
    {
        var source = await CreateSourceAsync(new
        {
            endpoint = FakeRestApi.Url,
            itemsPath = "items",
            headers = """{"X-Api-Key":"k-123"}"""
        }, "RestApi");

        Assert.True(source.Configuration!["hasKey"]!.GetValue<bool>());
        Assert.Null(source.Configuration["headers"]);

        var result = await SyncAsync(source.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.DocumentsProcessed);
        Assert.Equal(1, _fake.Hits);
    }
}
