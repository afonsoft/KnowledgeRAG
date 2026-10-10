using System.Net.Http.Headers;
using KnowledgeHub.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace KnowledgeHub.Tests.Performance;

/// <summary>
/// Sustained-load checks for the hot read paths. The user's requirement is
/// "more than 30 requests per second" — every scenario asserts ≥30 rps with
/// zero errors, plus a generous p95 bound so catastrophic latency regressions
/// also fail. Rate limiting is disabled in the fixture so the run measures
/// application throughput, not the limiter (limiter behavior is covered by
/// the integration suite).
/// </summary>
public class ApiLoadTests : IClassFixture<ApiLoadTests.Fixture>
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);
    private const int Workers = 8;
    private const double MinRps = 30;
    private const double MaxP95Ms = 2000;

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-perf-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "false",
                    ["Embeddings:Provider"] = "deterministic",
                    ["RateLimiting:Enabled"] = "false"
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch (IOException) { /* best effort */ }
        }
    }

    private readonly Fixture _factory;
    private readonly ITestOutputHelper _output;

    public ApiLoadTests(Fixture factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private void AssertHealthy(LoadReport report)
    {
        _output.WriteLine(report.ToString());
        Assert.Equal(0, report.Errors);
        Assert.True(report.RequestsPerSecond >= MinRps,
            $"expected ≥{MinRps} rps, got {report.RequestsPerSecond:F1}");
        Assert.True(report.P95Ms < MaxP95Ms,
            $"p95 latency {report.P95Ms:F0}ms exceeded {MaxP95Ms}ms");
    }

    [Fact]
    public async Task AgentCard_Anonymous_Sustains30Rps()
    {
        var client = _factory.CreateClient();
        var report = await LoadHarness.RunAsync("GET /.well-known/agent-card.json",
            Workers, Duration,
            _ => Task.FromResult<Func<CancellationToken, Task>>(async ct =>
                (await client.GetAsync("/.well-known/agent-card.json", ct)).EnsureSuccessStatusCode()));
        AssertHealthy(report);
    }

    [Fact]
    public async Task RestToolsList_Sustains30Rps()
    {
        var admin = await TestAuth.LoginAsync(_factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, "perf-tools");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var report = await LoadHarness.RunAsync("GET /api/tools/", Workers, Duration,
            _ => Task.FromResult<Func<CancellationToken, Task>>(async ct =>
                (await client.GetAsync("/api/tools/", ct)).EnsureSuccessStatusCode()));
        AssertHealthy(report);
    }

    [Fact]
    public async Task McpToolsList_Sustains30Rps()
    {
        var sessions = new List<TestMcp>();
        var report = await LoadHarness.RunAsync("MCP tools/list", Workers, Duration,
            async _ =>
            {
                var mcp = await TestMcp.ConnectAsync(_factory);
                sessions.Add(mcp);
                return async _ => await mcp.SendAsync("tools/list");
            });
        foreach (var s in sessions)
            await s.DisposeAsync();
        AssertHealthy(report);
    }

    [Fact]
    public async Task McpSearchKnowledge_Sustains30Rps()
    {
        var sessions = new List<TestMcp>();
        var report = await LoadHarness.RunAsync("MCP tools/call search_knowledge",
            Workers, Duration,
            async _ =>
            {
                var mcp = await TestMcp.ConnectAsync(_factory);
                sessions.Add(mcp);
                return async _ =>
                {
                    var result = await mcp.SendAsync("tools/call", new
                    {
                        name = "search_knowledge",
                        arguments = new { query = "cache performance", topK = 3 }
                    });
                    if (result.GetProperty("isError").GetBoolean())
                        throw new InvalidOperationException("tool returned isError");
                };
            });
        foreach (var s in sessions)
            await s.DisposeAsync();
        AssertHealthy(report);
    }
}
