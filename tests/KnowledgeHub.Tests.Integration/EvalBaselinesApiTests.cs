using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260925-new-endpoints-integration-tests RF-002: eval baselines —
// promote/list, 404 on missing run, named-baseline auto-compare, gate persistence.
public class EvalBaselinesApiTests : IClassFixture<EvalBaselinesApiTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-evalbl-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
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

    public EvalBaselinesApiTests(Fixture factory)
    {
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"evalbl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) { _ = ex.Message; } }

    private async Task SeedSource()
    {
        await File.WriteAllTextAsync(Path.Join(_dir, "alpha.txt"), "EVALBL-ALPHA baseline marker");
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"evalbl-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null)).EnsureSuccessStatusCode();
    }

    private async Task<Guid> RunEvalAsync(object? extra = null)
    {
        var dataset = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = "hit",
                question = "EVALBL-ALPHA",
                expectedUris = new[] { "alpha.txt" },
                mode = "lexical",
                topK = 5
            }
        });
        var payload = new Dictionary<string, object?> { ["dataset"] = dataset };
        if (extra is JsonElement e && e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                payload[p.Name] = p.Value;
        var response = await _client.PostAsJsonAsync("/api/eval/run", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runId").GetGuid();
    }

    [Fact]
    public async Task Baselines_Promote_And_List()
    {
        await SeedSource();
        var runId = await RunEvalAsync();
        var name = $"bl-{Guid.NewGuid():N}";

        var promote = await _client.PostAsJsonAsync("/api/eval/baselines", new { name, runId });
        promote.EnsureSuccessStatusCode();

        var baselines = await _client.GetFromJsonAsync<List<JsonElement>>("/api/eval/baselines");
        var bl = Assert.Single(baselines!, b => b.GetProperty("name").GetString() == name);
        Assert.Equal(runId, bl.GetProperty("evalRunId").GetGuid());
    }

    [Fact]
    public async Task Baselines_Promote_MissingRun_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/eval/baselines",
            new { name = $"bl-{Guid.NewGuid():N}", runId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_WithGate_PersistsGateResult()
    {
        await SeedSource();
        var runId = await RunEvalAsync(JsonDocument.Parse(
            """{"gate":[{"metric":"recall_at_k","direction":"gte","threshold":0.5}]}""").RootElement);

        var run = await _client.GetFromJsonAsync<JsonElement>($"/api/eval/runs/{runId}");
        var gate = run.GetProperty("gate");
        Assert.Equal(JsonValueKind.Object, gate.ValueKind);
        Assert.Equal("pass", gate.GetProperty("status").GetString());
    }
}
