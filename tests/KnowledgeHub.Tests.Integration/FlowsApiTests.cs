using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Shared;
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

    // ── F3: triggers, webhook, approval gate ─────────────────────────

    [Fact]
    public async Task Trigger_crud_and_webhook_invokes_flow_anonymously()
    {
        var echoEvent = new FlowDefinitionDto(
            [new FlowInputDto("q", "string", Required: true, null, null)],
            [
                new FlowStepDto("s1", "transform", null,
                    JsonNode.Parse("""{"template":"echo: {{vars.q}} src={{vars.event.source}}"}""")!.AsObject(), null),
                new FlowStepDto("out", "output", null,
                    JsonNode.Parse("""{"value":"{{steps.s1.output}}"}""")!.AsObject(), null),
            ]);
        var create = await _admin.PostAsJsonAsync("/api/flows",
            new CreateFlowRequest("Hooked Flow", "test flow", echoEvent));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var flow = (await create.Content.ReadFromJsonAsync<FlowDetailDto>())!;

        var created = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/triggers",
            new CreateFlowTriggerRequest("webhook", null, new JsonObject { ["q"] = "static-q" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var trigger = (await created.Content.ReadFromJsonAsync<FlowTriggerDto>())!;
        Assert.Equal("webhook", trigger.Kind);
        Assert.NotNull(trigger.WebhookUrl);
        Assert.StartsWith("fwt_", trigger.WebhookUrl.Split('/').Last());

        var listed = await _admin.GetFromJsonAsync<List<FlowTriggerDto>>(
            $"/api/flows/{flow.Flow.Id}/triggers");
        Assert.Single(listed!);

        // Anonymous POST to the secret URL runs the flow — static input q
        // fills the required input, the event lands in inputs.event.
        var anon = _factory.CreateClient();
        var hook = await anon.PostAsJsonAsync(new Uri(trigger.WebhookUrl!).PathAndQuery,
            new { source = "test-event" });
        Assert.True(hook.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);
        var result = (await hook.Content.ReadFromJsonAsync<FlowRunResultDto>())!;
        Assert.Equal("done", result.Status);
        Assert.Equal("echo: static-q src=test-event", result.Output!.GetValue<string>());

        // Unknown token → 404
        var missing = await anon.PostAsJsonAsync("/api/flowtriggers/fwt_" + new string('0', 48),
            new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Disable → webhook stops firing
        var toggled = await _admin.PutAsJsonAsync($"/api/flows/triggers/{trigger.Id}",
            new UpdateFlowTriggerRequest(false, null, null));
        toggled.EnsureSuccessStatusCode();
        var disabled = await anon.PostAsJsonAsync(new Uri(trigger.WebhookUrl!).PathAndQuery,
            new { });
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);

        var del = await _admin.DeleteAsync($"/api/flows/triggers/{trigger.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Empty((await _admin.GetFromJsonAsync<List<FlowTriggerDto>>(
            $"/api/flows/{flow.Flow.Id}/triggers"))!);
    }

    [Fact]
    public async Task Schedule_trigger_requires_minimum_interval()
    {
        var flow = await CreateFlowAsync("Scheduled Flow");

        var bad = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/triggers",
            new CreateFlowTriggerRequest("schedule", 10, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/triggers",
            new CreateFlowTriggerRequest("schedule", 300, new JsonObject { ["q"] = "tick" }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var trigger = (await ok.Content.ReadFromJsonAsync<FlowTriggerDto>())!;
        Assert.Equal(300, trigger.IntervalSeconds);
        Assert.Null(trigger.WebhookUrl);
    }

    [Fact]
    public async Task Approval_step_suspends_run_and_approve_resumes_it()
    {
        var def = new FlowDefinitionDto([], [
            new FlowStepDto("work", "transform", null,
                JsonNode.Parse("""{"template":"did-work"}""")!.AsObject(), null),
            new FlowStepDto("gate", "approval", null,
                JsonNode.Parse("""{"message":"ship it?"}""")!.AsObject(), null),
            new FlowStepDto("out", "output", null,
                JsonNode.Parse("""{"value":"{{steps.gate.output.approved}}"}""")!.AsObject(), null),
        ]);
        var create = await _admin.PostAsJsonAsync("/api/flows",
            new CreateFlowRequest("Gated Flow", null, def));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var flow = (await create.Content.ReadFromJsonAsync<FlowDetailDto>())!;

        var run = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/run",
            new FlowRunRequest(new JsonObject()));
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var waiting = (await run.Content.ReadFromJsonAsync<FlowRunResultDto>())!;
        Assert.Equal("waiting_approval", waiting.Status);
        Assert.NotNull(waiting.ApprovalId);
        Assert.Equal("waiting", waiting.Steps[1].Status);

        // The gate shows up in /api/approvals as a flow-requested approval.
        var pending = await _admin.GetFromJsonAsync<List<ApprovalDto>>("/api/approvals?status=pending");
        var approval = pending!.Single(a => a.Id == waiting.ApprovalId);
        Assert.Equal("flow", approval.RequestedBy);
        Assert.Equal("flow:gated_flow:gate", approval.ToolName);

        // Approve → endpoint resumes the run inline and returns its result.
        var approve = await _admin.PostAsJsonAsync(
            $"/api/approvals/{approval.Id}/approve", (object?)null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var resumed = await approve.Content.ReadFromJsonAsync<JsonObject>();
        var result = resumed!["run"]!.Deserialize<FlowRunResultDto>(SharedJson.Options);
        Assert.Equal("done", result!.Status);
        Assert.True(result.Output!.GetValue<bool>());

        var persisted = await _admin.GetFromJsonAsync<FlowRunResultDto>(
            $"/api/flowruns/{waiting.RunId}");
        Assert.Equal("done", persisted!.Status);

        // Double-resume is rejected.
        var again = await _admin.PostAsJsonAsync(
            $"/api/approvals/{approval.Id}/approve", (object?)null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Deny_marks_suspended_run_failed()
    {
        var def = new FlowDefinitionDto([], [
            new FlowStepDto("gate", "approval", null,
                JsonNode.Parse("""{"message":"ok?"}""")!.AsObject(), null),
            new FlowStepDto("out", "output", null,
                JsonNode.Parse("""{"value":"never"}""")!.AsObject(), null),
        ]);
        var create = await _admin.PostAsJsonAsync("/api/flows",
            new CreateFlowRequest("Denied Flow", null, def));
        var flow = (await create.Content.ReadFromJsonAsync<FlowDetailDto>())!;

        var run = await _admin.PostAsJsonAsync($"/api/flows/{flow.Flow.Id}/run",
            new FlowRunRequest(new JsonObject()));
        var waiting = (await run.Content.ReadFromJsonAsync<FlowRunResultDto>())!;

        var deny = await _admin.PostAsync($"/api/approvals/{waiting.ApprovalId}/deny", null);
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);
        var resumed = await deny.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("failed", resumed!["run"]!.Deserialize<FlowRunResultDto>(SharedJson.Options)!.Status);
    }
}
