using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260927-chunk-window-retrieval-and-autocut RF-004: the REST surface
// accepts windowSize/limitMode/autocutSensitivity and reports the applied mode.
public class AutocutSearchApiTests : IClassFixture<AutocutSearchApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-autocut-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
    }

    private readonly Fixture _factory;
    public AutocutSearchApiTests(Fixture factory) => _factory = factory;

    [Fact]
    public async Task Search_ReportsLimitModeApplied_AndTotalMatches()
    {
        var http = await TestAuth.LoginAsync(_factory);

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/search?query=anything&limitMode=fixed"));
        var root = doc.RootElement;

        Assert.Equal("fixed", root.GetProperty("limitModeApplied").GetString());
        Assert.Equal(0, root.GetProperty("totalMatches").GetInt32());
    }

    [Fact]
    public async Task Search_DefaultsToAutocut_FromAppSettings()
    {
        var http = await TestAuth.LoginAsync(_factory);

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/search?query=anything"));

        Assert.Equal("autocut", doc.RootElement.GetProperty("limitModeApplied").GetString());
    }

    [Fact]
    public async Task Search_InvalidParams_Return400()
    {
        var http = await TestAuth.LoginAsync(_factory);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.GetAsync("/api/search?query=q&limitMode=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.GetAsync("/api/search?query=q&windowSize=9")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.GetAsync("/api/search?query=q&autocutSensitivity=0")).StatusCode);
    }
}
