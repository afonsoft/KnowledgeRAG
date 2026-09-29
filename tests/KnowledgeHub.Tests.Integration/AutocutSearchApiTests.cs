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

    // SPEC-20260929-observability-and-tests-residual RF-006: the empty-DB tests
    // above cover parameter plumbing only — with a seeded index the autocut
    // mode and window expansion must actually operate on real hits.
    [Fact]
    public async Task Search_SeededDb_AutocutRunsOnRealHits()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var dir = Path.Join(Path.GetTempPath(), $"kh-autocut-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 8; i++)
                await File.WriteAllTextAsync(Path.Join(dir, $"filler{i}.md"),
                    $"# Filler {i}\n\nUnrelated paragraph about topic {i} with no matching token.");
            await File.WriteAllTextAsync(Path.Join(dir, "hit.md"),
                "# Hit\n\nThe freeze procedure uses token AUTOCUTZX99. " +
                "AUTOCUTZX99 appears again for lexical weight.");
            await File.WriteAllTextAsync(Path.Join(dir, "hit2.md"),
                "# Hit 2\n\nA second document referencing AUTOCUTZX99 once.");

            var created = await http.PostAsJsonAsync("/api/sources", new
            {
                name = $"autocut-{Guid.NewGuid():N}",
                type = "ObsidianVault",
                configuration = new { path = dir },
                isActive = true
            });
            created.EnsureSuccessStatusCode();
            var source = (await created.Content
                .ReadFromJsonAsync<KnowledgeHub.Shared.Contracts.KnowledgeSourceDto>())!;
            (await http.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
                .EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await http.GetStringAsync(
                "/api/search?query=AUTOCUTZX99&limitMode=autocut&contextExpand=window&windowSize=1"));
            var root = doc.RootElement;

            Assert.Equal("autocut", root.GetProperty("limitModeApplied").GetString());
            Assert.True(root.GetProperty("totalMatches").GetInt32() >= 2,
                "seeded index must return the two matching documents");
            Assert.True(root.GetProperty("results").GetArrayLength() >= 2);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
