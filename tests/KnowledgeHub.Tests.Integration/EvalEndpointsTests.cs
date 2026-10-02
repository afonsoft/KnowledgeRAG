using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-eval-harness ACs: run against a seeded index reports
/// hand-computable metrics; malformed datasets 400; compare deltas; faithfulness
/// skipped without a chat provider.
/// </summary>
public class EvalEndpointsTests : IClassFixture<EvalEndpointsTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-eval-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private readonly HttpClient _client;
    private readonly string _dir;

    public EvalEndpointsTests(Fixture factory)
    {
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    private async Task SeedSource()
    {
        await File.WriteAllTextAsync(Path.Join(_dir, "alpha.txt"),
            "EVALTOKEN-ALPHA knowledge about alpha systems");
        await File.WriteAllTextAsync(Path.Join(_dir, "beta.txt"),
            "EVALTOKEN-BETA knowledge about beta systems");
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"eval-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Run_ComputesRetrievalMetrics()
    {
        await SeedSource();
        var dataset = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = "hit-case",
                question = "EVALTOKEN-ALPHA",
                expectedUris = new[] { "alpha.txt" },
                mode = "lexical",
                topK = 5
            },
            new
            {
                id = "miss-case",
                question = "EVALTOKEN-ALPHA",
                expectedUris = new[] { "nonexistent.txt" },
                mode = "lexical",
                topK = 5
            }
        });

        var response = await _client.PostAsJsonAsync("/api/eval/run", new
        {
            dataset,
            mode = "lexical",
            topK = 5
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, report.GetProperty("cases").GetInt32());
        var metrics = report.GetProperty("metrics");
        // hit-case finds alpha.txt (recall 1), miss-case finds nothing (recall 0) → 0.5
        Assert.Equal(0.5, metrics.GetProperty("recallAtK").GetDouble());
        var results = report.GetProperty("results").EnumerateArray().ToList();
        Assert.True(results.First(r => r.GetProperty("caseId").GetString() == "hit-case").GetProperty("hit").GetBoolean());
        Assert.False(results.First(r => r.GetProperty("caseId").GetString() == "miss-case").GetProperty("hit").GetBoolean());
    }

    [Fact]
    public async Task Run_MalformedDataset_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/eval/run", new { dataset = "[]" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Run_CompareTo_ProducesDelta()
    {
        await SeedSource();
        var dataset = JsonSerializer.Serialize(new object[]
        {
            new { id = "c1", question = "EVALTOKEN-BETA", expectedUris = new[] { "beta.txt" }, mode = "lexical", topK = 5 }
        });

        var first = await (await _client.PostAsJsonAsync("/api/eval/run", new { dataset, mode = "lexical" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var runId = first.GetProperty("runId").GetGuid();

        // Second run expecting a missing uri → c1 hit→miss regression.
        var regressed = JsonSerializer.Serialize(new object[]
        {
            new { id = "c1", question = "EVALTOKEN-BETA", expectedUris = new[] { "gone.txt" }, mode = "lexical", topK = 5 }
        });
        var second = await (await _client.PostAsJsonAsync("/api/eval/run", new { dataset = regressed, mode = "lexical", compareTo = runId }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var delta = second.GetProperty("delta");
        Assert.Equal(runId, delta.GetProperty("compareRunId").GetGuid());
        Assert.Contains("c1", delta.GetProperty("regressions").EnumerateArray().Select(e => e.GetString()));
        Assert.True(delta.GetProperty("recallAtKDelta").GetDouble() < 0);
    }

    [Fact]
    public async Task Run_FaithfulnessLlm_WithoutProvider_Skips()
    {
        await SeedSource();
        var dataset = JsonSerializer.Serialize(new object[]
        {
            new { id = "c1", question = "EVALTOKEN-ALPHA", expectedUris = new[] { "alpha.txt" },
                  expectedTextMarkers = new[] { "alpha" }, mode = "lexical", topK = 5 }
        });

        var response = await _client.PostAsJsonAsync("/api/eval/run", new { dataset, faithfulness = "llm" });
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        var metrics = report.GetProperty("metrics");
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("faithfulness").ValueKind);
        Assert.Equal("chat provider not configured", metrics.GetProperty("faithfulnessSkippedReason").GetString());
    }

    [Fact]
    public async Task Runs_List_And_Get()
    {
        await SeedSource();
        var dataset = JsonSerializer.Serialize(new object[]
        {
            new { id = "c1", question = "EVALTOKEN-ALPHA", expectedUris = new[] { "alpha.txt" }, mode = "lexical" }
        });
        var run = await (await _client.PostAsJsonAsync("/api/eval/run", new { dataset }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("runId").GetGuid();

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/eval/runs");
        Assert.Contains(list.EnumerateArray(), r => r.GetProperty("id").GetGuid() == runId);

        var one = await _client.GetFromJsonAsync<JsonElement>($"/api/eval/runs/{runId}");
        Assert.Equal(runId, one.GetProperty("runId").GetGuid());

        var missing = await _client.GetAsync($"/api/eval/runs/{runId}?compare={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
