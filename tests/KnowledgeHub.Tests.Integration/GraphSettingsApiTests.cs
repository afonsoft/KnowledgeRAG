using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260923-graph-settings-ui RF-004/RF-005: /api/settings/graph round-trip,
// bounds validation, and live catalog visibility (enabling the graph surfaces
// find_dependencies in /api/tools without restart).
public class GraphSettingsApiTests : IClassFixture<GraphSettingsApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-graphsettings-{Guid.NewGuid():N}.db")
                }));
    }

    private readonly Fixture _factory;
    public GraphSettingsApiTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_GetGraph_Returns401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/settings/graph")).StatusCode);
    }

    [Fact]
    public async Task Get_Defaults_EnvSource()
    {
        var http = TestAuth.Login(_factory);
        await http.DeleteAsync("/api/settings/graph"); // shared fixture DB — reset

        var dto = await http.GetFromJsonAsync<JsonElement>("/api/settings/graph");
        Assert.Equal("env", dto.GetProperty("source").GetString());
        Assert.True(dto.GetProperty("enabled").GetBoolean()); // default ON
        Assert.Equal(200, dto.GetProperty("maxChunksPerSync").GetInt32());
        Assert.Equal(2000, dto.GetProperty("maxChunkChars").GetInt32());
        Assert.Equal(200, dto.GetProperty("maxResults").GetInt32());
    }

    [Fact]
    public async Task PutGetDelete_RoundTrip()
    {
        var http = TestAuth.Login(_factory);

        var put = await http.PutAsJsonAsync("/api/settings/graph", new
        {
            enabled = true,
            maxChunksPerSync = 50,
            maxChunkChars = 1000,
            maxResults = 100
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var dto = await http.GetFromJsonAsync<JsonElement>("/api/settings/graph");
        Assert.Equal("store", dto.GetProperty("source").GetString());
        Assert.True(dto.GetProperty("enabled").GetBoolean());
        Assert.Equal(50, dto.GetProperty("maxChunksPerSync").GetInt32());
        Assert.Equal(1000, dto.GetProperty("maxChunkChars").GetInt32());
        Assert.Equal(100, dto.GetProperty("maxResults").GetInt32());
        Assert.True(dto.TryGetProperty("updatedAt", out var ts) && ts.ValueKind != JsonValueKind.Null);

        Assert.Equal(HttpStatusCode.NoContent,
            (await http.DeleteAsync("/api/settings/graph")).StatusCode);
        var cleared = await http.GetFromJsonAsync<JsonElement>("/api/settings/graph");
        Assert.Equal("env", cleared.GetProperty("source").GetString());
        Assert.True(cleared.GetProperty("enabled").GetBoolean()); // env default ON
    }

    [Theory]
    [InlineData(0, 2000, 200)]    // maxChunksPerSync too low
    [InlineData(200, 100, 200)]   // maxChunkChars too low
    [InlineData(200, 2000, 5)]    // maxResults too low
    [InlineData(20001, 2000, 200)]// maxChunksPerSync too high
    public async Task Put_InvalidBounds_Returns400(int chunks, int chars, int results)
    {
        var http = TestAuth.Login(_factory);
        var response = await http.PutAsJsonAsync("/api/settings/graph", new
        {
            enabled = true,
            maxChunksPerSync = chunks,
            maxChunkChars = chars,
            maxResults = results
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EnableViaPut_SurfacesGraphTools_WithoutRestart()
    {
        var http = TestAuth.Login(_factory);
        // Graph:Enabled defaults to ON — write an explicit disabled baseline.
        await http.PutAsJsonAsync("/api/settings/graph", new
        {
            enabled = false,
            maxChunksPerSync = 200,
            maxChunkChars = 2000,
            maxResults = 200
        });

        var before = await http.GetFromJsonAsync<JsonElement>("/api/tools/");
        Assert.DoesNotContain(before.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()), n => n == "find_dependencies");

        await http.PutAsJsonAsync("/api/settings/graph", new
        {
            enabled = true,
            maxChunksPerSync = 200,
            maxChunkChars = 2000,
            maxResults = 200
        });

        var after = await http.GetFromJsonAsync<JsonElement>("/api/tools/");
        var names = after.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("find_dependencies", names);
        Assert.Contains("analyze_impact", names);

        await http.DeleteAsync("/api/settings/graph"); // leave no state behind
    }
}
