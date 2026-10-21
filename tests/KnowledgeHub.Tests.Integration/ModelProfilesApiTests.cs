using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20261021-model-capability-profiles RF-3/RF-6: merged catalog GET,
// user override PUT/DELETE round-trip.
public class ModelProfilesApiTests : IClassFixture<ModelProfilesApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-mprof-{Guid.NewGuid():N}.db")
                }));
        }
    }

    private readonly HttpClient _client;

    public ModelProfilesApiTests(Fixture factory) => _client = TestAuth.Login(factory);

    [Fact]
    public async Task Get_ReturnsBuiltinMergedCatalog()
    {
        var body = await _client.GetStringAsync("/api/settings/model-profiles");
        var doc = JsonDocument.Parse(body);
        var patterns = doc.RootElement.GetProperty("profiles")
            .EnumerateArray().Select(p => p.GetProperty("modelPattern").GetString()).ToList();
        Assert.Contains("gpt-4o*", patterns);
        Assert.Contains("*", patterns); // catch-all
    }

    [Fact]
    public async Task PutThenGetThenDelete_OverrideRoundTrips()
    {
        var put = await _client.PutAsJsonAsync("/api/settings/model-profiles/custom-*", new
        {
            contextWindowTokens = 4096,
            supportsToolCalls = false,
            supportsStructuredOutput = false,
            supportsReasoning = false
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var body = await _client.GetStringAsync("/api/settings/model-profiles");
        var profile = JsonDocument.Parse(body).RootElement.GetProperty("profiles")
            .EnumerateArray().First(p => p.GetProperty("modelPattern").GetString() == "custom-*");
        Assert.Equal("user", profile.GetProperty("source").GetString());
        Assert.Equal(4096, profile.GetProperty("contextWindowTokens").GetInt32());
        Assert.False(profile.GetProperty("supportsToolCalls").GetBoolean());

        var del = await _client.DeleteAsync("/api/settings/model-profiles/custom-*");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var del2 = await _client.DeleteAsync("/api/settings/model-profiles/custom-*");
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }
}
