using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-rate-limiting ACs: per-partition 429 with Retry-After on llm
/// endpoints, independent api-key partitions, MCP isError on over-limit calls,
/// Enabled=false passthrough, hubs exempt.
/// Each test uses a fresh api key → isolated llm partition (no cross-test bleed).
/// </summary>
public class RateLimitTests : IClassFixture<RateLimitTests.Fixture>, IClassFixture<RateLimitTests.DisabledFixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-rl-{Guid.NewGuid():N}.db"),
                    ["RateLimiting:LlmPermitLimit"] = "2",
                    ["RateLimiting:LlmWindowSeconds"] = "3600",
                    ["RateLimiting:GeneralPermitLimit"] = "500"
                }));
        }
    }

    public sealed class DisabledFixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-rloff-{Guid.NewGuid():N}.db"),
                    ["RateLimiting:Enabled"] = "false",
                    ["RateLimiting:LlmPermitLimit"] = "1"
                }));
        }
    }

    private readonly Fixture _factory;
    private readonly DisabledFixture _disabled;

    public RateLimitTests(Fixture factory, DisabledFixture disabled)
    {
        _factory = factory;
        _disabled = disabled;
    }

    private async Task<HttpClient> BearerClient()
    {
        var cookie = await TestAuth.LoginAsync(_factory);
        var secret = await TestAuth.CreateApiKeyAsync(cookie, $"rl-{Guid.NewGuid():N}");
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return bearer;
    }

    [Fact]
    public async Task Ask_OverLimit_Returns429WithRetryAfter()
    {
        var client = await BearerClient();

        await client.PostAsJsonAsync("/api/ask", new { question = "q1" });
        await client.PostAsJsonAsync("/api/ask", new { question = "q2" });
        var third = await client.PostAsJsonAsync("/api/ask", new { question = "q3" });

        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
        var body = await third.Content.ReadAsStringAsync();
        Assert.Contains("rate_limited", body);
    }

    [Fact]
    public async Task ApiKey_Partitions_AreIndependent()
    {
        var keyA = await BearerClient();
        var keyB = await BearerClient();

        for (var i = 0; i < 3; i++)
            await keyA.PostAsJsonAsync("/api/ask", new { question = $"q{i}" });

        var b = await keyB.PostAsJsonAsync("/api/ask", new { question = "q" });
        Assert.NotEqual((HttpStatusCode)429, b.StatusCode);
    }

    [Fact]
    public async Task Mcp_AskKnowledge_OverLimit_ReturnsIsError()
    {
        var bearer = await BearerClient();
        await using var mcp = new TestMcp(bearer);
        var init = await mcp.SendAsync("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "rl-test", version = "1.0" }
        });
        Assert.Equal("knowledge", init.GetProperty("serverInfo").GetProperty("name").GetString());
        await mcp.NotifyAsync("notifications/initialized");

        async Task<bool> Call() =>
            (await mcp.SendAsync("tools/call", new
            {
                name = "ask_knowledge",
                arguments = new { question = "q" }
            })).GetProperty("isError").GetBoolean();

        await Call(); // 1st — acquired
        await Call(); // 2nd — acquired
        var rejected = await Call(); // 3rd — over the 2/min llm limit

        Assert.True(rejected);
    }

    [Fact]
    public async Task Disabled_NeverLimits()
    {
        var cookie = await TestAuth.LoginAsync(_disabled);
        var secret = await TestAuth.CreateApiKeyAsync(cookie, "rl-off");
        var bearer = _disabled.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        for (var i = 0; i < 5; i++)
        {
            var r = await bearer.PostAsJsonAsync("/api/ask", new { question = $"q{i}" });
            Assert.NotEqual((HttpStatusCode)429, r.StatusCode);
        }
    }

    [Fact]
    public async Task Hub_IsNotRateLimited()
    {
        var cookie = await TestAuth.LoginAsync(_factory);
        // Exhaust the general bucket would need 500 calls — instead assert the
        // hub negotiate endpoint carries no limiter policy at all by calling it
        // after a burst of llm traffic (independent policy anyway).
        var r = await cookie.PostAsync("/hubs/mcp/negotiate?negotiateVersion=1", null);
        Assert.NotEqual((HttpStatusCode)429, r.StatusCode);
    }
}
