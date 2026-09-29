using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;

namespace KnowledgeHub.Tests.Unit.Resilience;

// SPEC-20260928-resilience-tool-fallback-wiring RF-001/RF-002/RF-003:
// transient failures on a read-only tool fall through to the next provider
// in the same capability; permanent failures and caller-scope boundaries are
// never crossed; disabled mode is a pass-through.
public sealed class ResilientToolInvokerTests
{
    private static CatalogTool Tool(
        string name, bool readOnly,
        Func<ToolCallContext, CancellationToken, ValueTask<CallToolResult>> handler) =>
        new()
        {
            Name = name,
            Description = name,
            InputSchema = new JsonObject(),
            ReadOnly = readOnly,
            Handler = handler
        };

    private static CatalogTool OkTool(string name) =>
        Tool(name, true, (_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = $"ok:{name}" }]
            }));

    private static IServiceProvider Services(string mode = "enforce", int max = 2)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IFallbackPolicyEngine>(_ => new FallbackPolicyEngine(
            Options.Create(new FallbackOptions { Mode = mode, MaxFallbackAttempts = max }),
            NullLogger<FallbackPolicyEngine>.Instance));
        sc.AddSingleton<ToolCapabilityRegistry>();
        return sc.BuildServiceProvider();
    }

    private static ToolCallContext Ctx(IServiceProvider sp) =>
        new() { Services = sp, Arguments = null };

    [Fact]
    public async Task Disabled_PassesThroughWithoutFallback()
    {
        var primary = Tool("tavily_search", true, (_, _) =>
            throw new HttpRequestException("socket"));
        var wrapped = ResilientToolInvoker.Wrap([primary, OkTool("firecrawl_search")]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => wrapped[0].Handler(Ctx(Services("disabled")), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Enforce_ExceptionFallbacksToNextProvider()
    {
        var primary = Tool("tavily_search", true, (_, _) =>
            throw new HttpRequestException("socket"));
        var wrapped = ResilientToolInvoker.Wrap([primary, OkTool("firecrawl_search")]);

        var result = await wrapped[0].Handler(Ctx(Services()), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ok:firecrawl_search",
            Assert.IsType<TextContentBlock>(result.Content![0]).Text);
    }

    [Fact]
    public async Task Enforce_IsErrorTransientFallbacks()
    {
        var primary = Tool("tavily_search", true, (_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "tavily upstream error: 503 service unavailable" }]
            }));
        var wrapped = ResilientToolInvoker.Wrap([primary, OkTool("firecrawl_search")]);

        var result = await wrapped[0].Handler(Ctx(Services()), CancellationToken.None);

        Assert.Equal("ok:firecrawl_search",
            Assert.IsType<TextContentBlock>(result.Content![0]).Text);
    }

    [Fact]
    public async Task Enforce_IsErrorPermanent_SurfacesAsIs()
    {
        var primary = Tool("tavily_search", true, (_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "401 unauthorized — invalid api key" }]
            }));
        var wrapped = ResilientToolInvoker.Wrap([primary, OkTool("firecrawl_search")]);

        var result = await wrapped[0].Handler(Ctx(Services()), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("401", Assert.IsType<TextContentBlock>(result.Content![0]).Text);
    }

    [Fact]
    public async Task Enforce_PermanentException_Rethrows()
    {
        var primary = Tool("tavily_search", true, (_, _) =>
            throw new HttpRequestException("bad request", null,
                System.Net.HttpStatusCode.BadRequest));
        var wrapped = ResilientToolInvoker.Wrap([primary, OkTool("firecrawl_search")]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => wrapped[0].Handler(Ctx(Services()), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CandidateOutsideCallerCatalog_IsSkipped()
    {
        // tavily fails but firecrawl_search is not in the caller's visible
        // set — no candidate → original failure propagates (RF-002).
        var primary = Tool("tavily_search", true, (_, _) =>
            throw new HttpRequestException("socket"));
        var wrapped = ResilientToolInvoker.Wrap([primary]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => wrapped[0].Handler(Ctx(Services()), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Budget_StopsAfterMaxAttempts()
    {
        var calls = 0;
        var fail = Tool("tavily_search", true, (_, _) =>
            throw new HttpRequestException("socket"));
        var fail2 = Tool("firecrawl_search", true, (_, _) =>
        {
            calls++;
            throw new HttpRequestException("socket");
        });
        var wrapped = ResilientToolInvoker.Wrap([fail, fail2]);

        // MaxAttempts=1 → one fallback try, then budget exhausted.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => wrapped[0].Handler(Ctx(Services(max: 1)), CancellationToken.None).AsTask());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WriteTool_NeverWrapped()
    {
        var write = Tool("tavily_search", readOnly: false, (_, _) =>
            throw new HttpRequestException("socket"));
        var wrapped = ResilientToolInvoker.Wrap([write, OkTool("firecrawl_search")]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => wrapped[0].Handler(Ctx(Services()), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task UnmappedTool_PassesThrough()
    {
        var other = Tool("read_document", true, (_, _) =>
            throw new HttpRequestException("socket"));
        var wrapped = ResilientToolInvoker.Wrap([other]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => wrapped[0].Handler(Ctx(Services()), CancellationToken.None).AsTask());
    }
}
