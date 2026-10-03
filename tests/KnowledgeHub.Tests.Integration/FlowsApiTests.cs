using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// Agent flows (UI-defined, AnythingLLM/Dify model): REST CRUD + validate + run
/// + run history, and the MCP surface — list_flows / run_flow / flow_&lt;slug&gt;
/// appearing in tools/list for enabled flows.
/// </summary>
public class FlowsApiTests : IClassFixture<FlowsApiTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-flows-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(DbPath))
                File.Delete(DbPath);
        }
    }

    private readonly Fixture _factory;
    private readonly HttpClient _admin;

    public FlowsApiTests(Fixture factory)
    {
        _factory = factory;
        _admin = TestAuth.LoginAsync(factory).GetAwaiter().GetResult();
    }

    public void Dispose() => _admin.Dispose();

    private static FlowDefinitionDto EchoDefinition() => new(
        [new FlowInputDto("q", "string", Required: true, null, null)],
        [
            new FlowStepDto("s1", "transform", null,
                JsonNode.Parse("""{"template":"echo: {{vars.q}}"}""")!.AsObject(), null),
            new FlowStepDto("out", "output", null,
                JsonNode.Parse("""{"value":"{{steps.s1.output}}"}""")!.AsObject(), null),
        ]);

    private async Task<FlowDetailDto> CreateFlowAsync(string name = "Echo Flow")
    {
        var response = await _admin.PostAsJsonAsync("/api/flows",
            new CreateFlowRequest(name, "test flow", EchoDefinition()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FlowDetailDto>())!;
    }

    [Fact]
    public async Task Create_List_Get_Update_Delete_flow()
    {
        var created = await CreateFlowAsync();
        Assert.Equal("Echo Flow", created.Flow.Name);
        Assert.Equal("echo_flow", created.Flow.Slug);
        Assert.True(created.Flow.Enabled);
        Assert.Equal(1, created.Flow.Version);

        var list = await _admin.GetFromJsonAsync<List<FlowDto>>("/api/flows");
        Assert.Contains(list!, f => f.Id == created.Flow.Id);

        var detail = await _admin.GetFromJsonAsync<FlowDetailDto>($"/api/flows/{created.Flow.Id}");
        Assert.Equal(2, detail!.Definition.Steps.Count);

        var update = await _admin.PutAsJsonAsync($"/api/flows/{created.Flow.Id}",
            new UpdateFlowRequest("Echo Renamed", null, Enabled: false, null));
        update.EnsureSuccessStatusCode();
        var renamed = await _admin.GetFromJsonAsync<FlowDetailDto>($"/api/flows/{created.Flow.Id}");
        Assert.Equal("Echo Renamed", renamed!.Flow.Name);
        Assert.False(renamed.Flow.Enabled);
        Assert.Equal("echo_flow", renamed.Flow.Slug); // slug is stable across renames

        var delete = await _admin.DeleteAsync($"/api/flows/{created.Flow.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.GetAsync($"/api/flows/{created.Flow.Id}")).StatusCode);
    }

    [Fact]
    public async Task Validate_accepts_good_and_rejects_bad_definitions()
    {
        var ok = await _admin.PostAsJsonAsync("/api/flows/validate",
            new ValidateFlowRequest(EchoDefinition()));
        ok.EnsureSuccessStatusCode();

        var bad = new FlowDefinitionDto([],
            [new FlowStepDto("s1", "no_such_type", null, null, null)]);
        var rejected = await _admin.PostAsJsonAsync("/api/flows/validate",
            new ValidateFlowRequest(bad));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Run_flow_via_rest_and_inspect_run_history()
    {
        var flow = await CreateFlowAsync("Echo Runner");

        var run = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/run",
            new FlowRunRequest(new JsonObject { ["q"] = "rrf" }));
        run.EnsureSuccessStatusCode();
        var result = await run.Content.ReadFromJsonAsync<FlowRunResultDto>();
        Assert.Equal("done", result!.Status);
        Assert.Equal("echo: rrf", result.Output!.GetValue<string>());
        Assert.Equal(2, result.Steps.Count);

        var runs = await _admin.GetFromJsonAsync<List<FlowRunDto>>($"/api/flows/{flow.Flow.Id}/runs");
        var persisted = Assert.Single(runs!);
        Assert.Equal(result.RunId, persisted.Id);
        Assert.Equal("done", persisted.Status);

        var missing = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/run",
            new FlowRunRequest(new JsonObject()));
        // failed run (required input missing) → 422 carrying the run result
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var failed = await missing.Content.ReadFromJsonAsync<FlowRunResultDto>();
        Assert.Equal("failed", failed!.Status);
    }

    [Fact]
    public async Task Flows_require_authentication()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/flows")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/flows",
                new CreateFlowRequest("x", null, EchoDefinition()))).StatusCode);
    }

    [Fact]
    public async Task Enabled_flow_appears_as_mcp_tool_and_runs_via_mcp()
    {
        var flow = await CreateFlowAsync("MCP Echo");

        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var tools = await mcp.SendAsync("tools/list");
        var names = TestMcp.ToolNames(tools);

        Assert.Contains("list_flows", names);
        Assert.Contains("run_flow", names);
        Assert.Contains("flow_mcp_echo", names);

        // flow_<slug> inputSchema exposes the declared inputs
        var flowTool = tools.GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "flow_mcp_echo");
        var schema = flowTool.GetProperty("inputSchema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("q", out _));
        Assert.Contains("q", schema.GetProperty("required").EnumerateArray()
            .Select(r => r.GetString()));

        var called = await mcp.SendAsync("tools/call", new
        {
            name = "flow_mcp_echo",
            arguments = new { q = "via-mcp" }
        });
        var text = called.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("echo: via-mcp", text);

        // disabling removes the direct tool (list changed on next tools/list)
        await _admin.PutAsJsonAsync($"/api/flows/{flow.Flow.Id}",
            new UpdateFlowRequest(null, null, Enabled: false, null));
        var toolsAfter = await mcp.SendAsync("tools/list");
        Assert.DoesNotContain("flow_mcp_echo", TestMcp.ToolNames(toolsAfter));

        // run_flow rejects a disabled flow explicitly
        var viaGeneric = await mcp.SendAsync("tools/call", new
        {
            name = "run_flow",
            arguments = new { flow = "mcp_echo", inputs = new { q = "generic" } }
        });
        Assert.True(viaGeneric.GetProperty("isError").GetBoolean());
        Assert.Contains("not found or disabled",
            viaGeneric.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task List_flows_tool_reports_enabled_state()
    {
        await CreateFlowAsync("Listed Flow");
        await using var mcp = await TestMcp.ConnectAsync(_factory);
        var result = await mcp.SendAsync("tools/call", new
        {
            name = "list_flows",
            arguments = new { }
        });
        var items = result.GetProperty("structuredContent").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.Contains(items, f => f.GetProperty("slug").GetString() == "listed_flow"
            && f.GetProperty("tool").GetString() == "flow_listed_flow");
    }
}
