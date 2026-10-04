using KnowledgeHub.McpEngine;
using KnowledgeHub.McpEngine.Activity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace KnowledgeHub.Tests.Unit.McpEngine;

// Covers the composition root (SPEC-01): service registrations, transport
// mode mapping and options wiring (filters + server info).
public sealed class McpServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void AddKnowledgeHubMcp_RegistersFeedGateRegistryAndMetrics()
    {
        var services = new ServiceCollection();
        services.AddKnowledgeHubMcp(Config());
        var sp = services.BuildServiceProvider();

        Assert.IsType<McpActivityFeed>(sp.GetRequiredService<IMcpActivityFeed>());
        Assert.IsType<SessionCallGate>(sp.GetRequiredService<SessionCallGate>());
        Assert.NotNull(sp.GetRequiredService<McpSessionRegistry>());
        Assert.NotNull(sp.GetRequiredService<IMcpRequestMetrics>()); // NullMcpRequestMetrics fallback
    }

    [Fact]
    public void AddKnowledgeHubMcp_WiresFiltersIntoServerOptions()
    {
        var services = new ServiceCollection();
        services.AddKnowledgeHubMcp(Config());
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;
        Assert.Equal("knowledge", options.ServerInfo!.Name);
        Assert.NotNull(options.Capabilities!.Tools);
        Assert.Single(options.Filters!.Request!.CallToolFilters!);
        Assert.Single(options.Filters.Message!.IncomingFilters!);
    }

    [Fact]
    public void AddKnowledgeHubMcp_HonoursFeedCapacityAndGateLimit()
    {
        var services = new ServiceCollection();
        services.AddKnowledgeHubMcp(Config(
            ("Mcp:ActivityFeedCapacity", "42"),
            ("Mcp:MaxConcurrentCallsPerSession", "3")));
        var sp = services.BuildServiceProvider();
        Assert.Equal(42, sp.GetRequiredService<IMcpActivityFeed>().Capacity);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("7")]
    public void ParseSessionMode_InvalidValue_Throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => McpServiceCollectionExtensions.ParseSessionMode(value));
        Assert.Contains("Mcp:SessionMode", ex.Message);
    }
}
