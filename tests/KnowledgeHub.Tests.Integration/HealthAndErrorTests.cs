using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-health-checks (/health/live + /health/ready) and
// SPEC-20260914-error-handling (RFC 7807 ProblemDetails, no stack traces).
public class HealthAndErrorTests
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    public sealed class BrokenEmbeddingFixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    // Passes config validation (valid URI) but is unreachable at runtime.
                    ["Embeddings:Provider"] = "ollama",
                    ["Embeddings:Endpoint"] = "http://127.0.0.1:1",
                    ["Embeddings:Model"] = "nomic-embed-text"
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task HealthLive_Returns200()
    {
        // Covers AC: /health/live → 200 when Kestrel is running
        await using var factory = new Fixture();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns200_WhenDbEmbeddingsIngestionOk()
    {
        // Covers AC: /health/ready → 200 when DB migrated, embedding provider ready, ingestion initialized
        await using var factory = new Fixture();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnhandledError_ReturnsProblemDetails_WithoutStackTrace()
    {
        // Covers AC: unhandled exception → application/problem+json, no stack trace
        await using var factory = new BrokenEmbeddingFixture();
        using var client = await TestAuth.LoginAsync(factory);

        // An explicit sourceId keeps the pipeline running (empty source lists
        // short-circuit before embedding since SPEC-20260923-source-authorization).
        var response = await client.GetAsync($"/api/search?query=test&sourceId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":500", body);
        Assert.DoesNotContain(" at ", body); // no stack frames leaked
    }
}
