using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-02 ACs: CRUD, validation, 409 duplicate, config redaction, search over active sources.
public class SourcesApiTests : IClassFixture<SourcesApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"kh-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) TryDeleteDb();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            TryDeleteDb();
        }

        private void TryDeleteDb()
        {
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    private readonly HttpClient _client;

    public SourcesApiTests(Fixture factory) => _client = TestAuth.Login(factory);

    private static object VaultPayload(string name) => new
    {
        name,
        description = "test vault",
        type = "ObsidianVault",
        configuration = new { path = "/tmp/vault" },
        isActive = true
    };

    [Fact]
    public async Task Post_ValidSource_Returns201_AndAppearsInList()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", VaultPayload($"vault-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>();
        Assert.NotNull(created);

        var list = await _client.GetFromJsonAsync<List<KnowledgeSourceDto>>("/api/sources");
        Assert.Contains(list!, s => s.Id == created!.Id);
    }

    [Fact]
    public async Task Post_DuplicateName_Returns409()
    {
        var name = $"dup-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/sources", VaultPayload(name));
        var response = await _client.PostAsJsonAsync("/api/sources", VaultPayload(name));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_MissingRequiredConfigKey_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"bad-{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { notPath = "x" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_InvalidType_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"badtype-{Guid.NewGuid():N}",
            type = "NotAType",
            configuration = new { path = "/tmp/x" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_MissingSource_Returns404()
    {
        var response = await _client.GetAsync($"/api/sources/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Source_RedactsSensitiveConfiguration()
    {
        // SPEC-20260927-restapi-sqldatabase-connectors RF-006: the connection
        // string is lifted to the encrypted store — GET echoes only hasKey.
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"sql-{Guid.NewGuid():N}",
            type = "SqlDatabase",
            configuration = new { provider = "sqlite", query = "SELECT 1", connectionString = "Data Source=x.db" }
        });
        var created = await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>();
        Assert.True(created!.Configuration!["hasKey"]!.GetValue<bool>());
        Assert.Null(created.Configuration["connectionString"]);
        Assert.Equal("SELECT 1", created.Configuration["query"]!.GetValue<string>());
        Assert.Equal("sqlite", created.Configuration["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Post_McpProxy_KeyGoesToSecretStore_NeverEchoed()
    {
        // SPEC-20260917-mcp-proxy-source-type RF-001/RF-003: apiKey is lifted
        // to the encrypted store; GET returns only hasKey.
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"mcp-{Guid.NewGuid():N}",
            type = "McpProxy",
            configuration = new { endpoint = "https://mcp.example.com/mcp", apiKey = "secret-key-1", namePrefix = "ex_" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>();
        Assert.NotNull(created);
        Assert.False(created!.Configuration!.ContainsKey("apiKey"));
        Assert.True(created.Configuration["hasKey"]!.GetValue<bool>());

        var raw = await _client.GetStringAsync($"/api/sources/{created.Id}");
        Assert.DoesNotContain("secret-key-1", raw);
    }

    [Fact]
    public async Task Post_McpProxy_InvalidEndpointOrTransport_Returns400()
    {
        var badEndpoint = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"mcp-bad-{Guid.NewGuid():N}",
            type = "McpProxy",
            configuration = new { endpoint = "ftp://x" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badEndpoint.StatusCode);

        var badTransport = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"mcp-bad2-{Guid.NewGuid():N}",
            type = "McpProxy",
            configuration = new { endpoint = "https://x.example/mcp", transport = "grpc" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTransport.StatusCode);
    }

    [Fact]
    public async Task Deactivate_ExcludesSourceFromSearch()
    {
        var name = $"deact-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync("/api/sources", VaultPayload(name));
        var created = await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>();

        var deactivate = await _client.PostAsync($"/api/sources/{created!.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var active = await _client.GetFromJsonAsync<List<KnowledgeSourceDto>>("/api/sources?active=true");
        Assert.DoesNotContain(active!, s => s.Id == created.Id);
    }

    [Fact]
    public async Task Delete_CascadesDocuments()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", VaultPayload($"del-{Guid.NewGuid():N}"));
        var created = await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>();

        var del = await _client.DeleteAsync($"/api/sources/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var docs = await _client.GetAsync($"/api/sources/{created.Id}/documents");
        Assert.Equal(HttpStatusCode.NotFound, docs.StatusCode);
    }

    [Fact]
    public async Task Sync_UnimplementedConnector_ReturnsSkipped()
    {
        // McpProxy is proxy-only (never ingestible) → sync lands on "skipped".
        // (RestApi/SqlDatabase became real connectors in
        // SPEC-20260927-restapi-sqldatabase-connectors.)
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"sync-{Guid.NewGuid():N}",
            type = "McpProxy",
            configuration = new { endpoint = "https://mcp.example.com/mcp" }
        });
        var created = await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>();

        var sync = await _client.PostAsync($"/api/sources/{created!.Id}/sync?wait=true", null);
        Assert.Equal(HttpStatusCode.Accepted, sync.StatusCode);
        var result = await sync.Content.ReadFromJsonAsync<SyncResultDto>();
        Assert.Equal("skipped", result!.Status);
    }

    [Fact]
    public async Task Search_EmptyCorpus_ReturnsEmptyResults()
    {
        var response = await _client.GetFromJsonAsync<SearchResponse>("/api/search?query=anything");
        Assert.NotNull(response);
        Assert.Empty(response!.Results);
    }

    [Fact]
    public async Task Search_MissingQuery_Returns400()
    {
        var response = await _client.GetAsync("/api/search?topK=5");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_TopKClamped_AcceptsZero()
    {
        // topK=0 clamps to default — must not error
        var response = await _client.GetAsync("/api/search?query=x&topK=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
