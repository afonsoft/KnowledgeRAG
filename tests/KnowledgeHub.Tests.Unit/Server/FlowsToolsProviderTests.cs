using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.ToolProviders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers the flow-tool catalog (SPEC agent-flows): list_flows / run_flow plus
// each enabled flow re-exported as flow_<slug>, read-only flagging, and the
// not-found/disabled error paths.
public sealed class FlowsToolsProviderTests
{
    private static async Task<(SqliteConnection, KnowledgeHubDbContext)> NewDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    private const string SimpleFlowJson = """
        {"inputs":[{"name":"topic","type":"string","required":true,"description":"Theme"}],
         "steps":[{"id":"s1","type":"knowledge","name":"search","config":{"query":"x"}}]}
        """;

    private const string HttpFlowJson = """
        {"inputs":[],"steps":[{"id":"s1","type":"http","name":"call","config":{"url":"https://x"}}]}
        """;

    private static AgentFlow Flow(string slug, string json, bool enabled = true, string? desc = "d") =>
        new()
        {
            Name = $"Flow {slug}",
            Slug = slug,
            Description = desc,
            Enabled = enabled,
            DefinitionJson = json
        };

    private sealed class StaticToolProvider(IReadOnlyList<CatalogTool> tools) : IToolProvider
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider s, CancellationToken ct) =>
            Task.FromResult(tools);
    }

    private sealed class ThrowingToolProvider : IToolProvider
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider s, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static CatalogTool RoTool(string name) => new()
    {
        Name = name,
        Description = name,
        InputSchema = new System.Text.Json.Nodes.JsonObject(),
        ReadOnly = true,
        Handler = (_, _) => new ValueTask<CallToolResult>(new CallToolResult { IsError = false })
    };

    /// <summary>providerScope mimics the real DI scope: FlowsToolsProvider plus
    /// any peer providers whose tools feed the read-only map.</summary>
    private static (FlowsToolsProvider provider, IServiceProvider services) Build(
        KnowledgeHubDbContext db, params IToolProvider[] peers)
    {
        var provider = new FlowsToolsProvider(null!); // replaced below via scope
        var services = new ServiceCollection()
            .AddSingleton(db)
            .BuildServiceProvider();
        var providerScopeServices = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IToolProvider>(provider);
        foreach (var p in peers)
            providerScopeServices.AddSingleton(p);
        var providerScope = providerScopeServices.BuildServiceProvider();
        return (new FlowsToolsProvider(providerScope), services);
    }

    private static ToolCallContext Ctx(IServiceProvider services, string json = "{}") => new()
    {
        Services = services,
        Arguments = JsonDocument.Parse(json).RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
    };

    private static string TextOf(CallToolResult r) =>
        Assert.IsType<TextContentBlock>(r.Content[0]).Text;

    [Fact]
    public async Task GetTools_EmitsCoreToolsPlusEnabledFlows()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.AddRange(
            Flow("weekly", SimpleFlowJson),
            Flow("off", SimpleFlowJson, enabled: false));
        await db.SaveChangesAsync();

        var (provider, services) = Build(db);
        var tools = await provider.GetToolsAsync(services, default);

        Assert.Contains(tools, t => t.Name == "list_flows");
        Assert.Contains(tools, t => t.Name == "run_flow");
        var flowTool = tools.Single(t => t.Name == "flow_weekly");
        Assert.Contains("[UI-defined flow]", flowTool.Description);
        Assert.DoesNotContain(tools, t => t.Name == "flow_off");
    }

    [Fact]
    public async Task FlowTool_SchemaCarriesDeclaredInputs()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.Add(Flow("schematized", SimpleFlowJson));
        await db.SaveChangesAsync();

        var (provider, services) = Build(db);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "flow_schematized");

        var schema = tool.InputSchema;
        Assert.True(schema["properties"]!["topic"] is not null);
        Assert.Equal("string", schema["properties"]!["topic"]!["type"]!.GetValue<string>());
        Assert.Equal("Theme", schema["properties"]!["topic"]!["description"]!.GetValue<string>());
        Assert.Equal("topic", schema["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task FlowTool_ReadOnlyWhenAllStepsReadOnly_NotReadOnlyForHttp()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.AddRange(
            Flow("searchy", SimpleFlowJson),   // knowledge step → search_knowledge
            Flow("fetchy", HttpFlowJson));     // http step → never read-only
        await db.SaveChangesAsync();

        var (provider, services) = Build(db, new StaticToolProvider([RoTool("search_knowledge")]));
        var tools = await provider.GetToolsAsync(services, default);

        Assert.True(tools.Single(t => t.Name == "flow_searchy").ReadOnly);
        Assert.False(tools.Single(t => t.Name == "flow_fetchy").ReadOnly);
    }

    [Fact]
    public async Task ReadOnlyMap_PeerProviderFailure_DoesNotBreakCatalog()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.Add(Flow("resilient", SimpleFlowJson));
        await db.SaveChangesAsync();

        var (provider, services) = Build(db, new ThrowingToolProvider());
        var tools = await provider.GetToolsAsync(services, default);
        Assert.Contains(tools, t => t.Name == "flow_resilient");
        // unknown tool names → not read-only (safe default)
        Assert.False(tools.Single(t => t.Name == "flow_resilient").ReadOnly);
    }

    [Fact]
    public async Task ListFlows_ReturnsEnabledOnly()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.AddRange(
            Flow("alpha", SimpleFlowJson, desc: "first"),
            Flow("beta", SimpleFlowJson, enabled: false));
        await db.SaveChangesAsync();

        var (provider, services) = Build(db);
        var listFlows = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "list_flows");

        var result = await listFlows.Handler(Ctx(services), default);
        Assert.False(result.IsError);
        var text = TextOf(result);
        Assert.Contains("flow_alpha", text);
        Assert.DoesNotContain("flow_beta", text);
        Assert.NotNull(result.StructuredContent);
    }

    [Fact]
    public async Task ListFlows_Empty_FriendlyText()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;

        var (provider, services) = Build(db);
        var listFlows = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "list_flows");
        var result = await listFlows.Handler(Ctx(services), default);
        Assert.Equal("No enabled flows.", TextOf(result));
    }

    [Fact]
    public async Task RunFlow_MissingArg_ThrowsInvalidParams()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        var (provider, services) = Build(db);
        var runFlow = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "run_flow");
        await Assert.ThrowsAsync<McpProtocolException>(async () => await runFlow.Handler(Ctx(services), default));
    }

    [Fact]
    public async Task RunFlow_UnknownSlug_ReturnsErrorResult()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.Add(Flow("real", SimpleFlowJson));
        await db.SaveChangesAsync();

        var (provider, services) = Build(db);
        var runFlow = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "run_flow");
        var result = await runFlow.Handler(Ctx(services, """{"flow":"ghost","inputs":{"a":1}}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("not found or disabled", TextOf(result));
    }

    [Fact]
    public async Task RunFlow_ByGuid_ResolvesFlow()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        var flow = Flow("guided", SimpleFlowJson);
        db.AgentFlows.Add(flow);
        await db.SaveChangesAsync();

        var (provider, services) = Build(db);
        var runFlow = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "run_flow");
        // No FlowService registered → InvokeFlowAsync throws on
        // GetRequiredService, proving the GUID lookup resolved (a miss would
        // have returned "not found or disabled" earlier).
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runFlow.Handler(
            Ctx(services, "{\"flow\":\"" + flow.Id + "\"}"), default));
    }

    [Fact]
    public async Task FlowSlugHandler_UnknownService_ReportsFailure()
    {
        var (conn, db) = await NewDbAsync();
        await using var _ = conn;
        db.AgentFlows.Add(Flow("invocable", SimpleFlowJson));
        await db.SaveChangesAsync();

        var (provider, services) = Build(db);
        var tool = (await provider.GetToolsAsync(services, default)).Single(t => t.Name == "flow_invocable");
        // flow_<slug> args are the inputs object; FlowService absent →
        // InvokeFlowAsync throws on GetRequiredService.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await tool.Handler(Ctx(services, """{"topic":"edge cases"}"""), default));
    }
}
