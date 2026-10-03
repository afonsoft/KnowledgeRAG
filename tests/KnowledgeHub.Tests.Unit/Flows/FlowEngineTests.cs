using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Flows;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Flows;

/// <summary>Sequential FlowEngine: input binding, step ordering, templating,
/// nesting (condition/foreach), failure + caps.</summary>
public sealed class FlowEngineTests
{
    private static FlowEngine NewEngine(IConfiguration? config = null) => new(
        new IFlowStepHandler[]
        {
            new ToolStepHandler(), new KnowledgeStepHandler(), new LlmStepHandler(),
            new HttpStepHandler(), new ConditionStepHandler(), new ForEachStepHandler(),
            new TransformStepHandler(), new OutputStepHandler(), new ApprovalStepHandler(),
            new FailStepHandler(),
        },
        config ?? new ConfigurationBuilder().AddInMemoryCollection().Build());

    private static IServiceProvider Services(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static FlowStepDto Step(string id, string type, object? config = null) =>
        new(id, type, null,
            config is null ? null : JsonNode.Parse(JsonSerializer.Serialize(config))!.AsObject(),
            null);

    [Fact]
    public async Task Missing_required_input_fails_before_steps()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [new FlowInputDto("q", "string", Required: true, null, null)],
            [Step("s1", "output", new { value = "never" })]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("q", result.Error);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task Default_input_binds_and_templates()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [new FlowInputDto("who", "string", false, null, JsonValue.Create("world")!)],
            [Step("s1", "output", new { value = "hello {{vars.who}}" })]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Equal("hello world", result.Output!.GetValue<string>());
    }

    [Fact]
    public async Task Steps_run_in_order_and_outputs_are_chainable()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [],
            [
                Step("t1", "transform", new { template = "first" }),
                Step("t2", "transform", new { template = "{{steps.t1.output}} second" }),
                Step("out", "output", new { value = "{{steps.t2.output}}" }),
            ]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Equal("first second", result.Output!.GetValue<string>());
        Assert.Equal(3, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.Equal("done", s.Status));
    }

    [Fact]
    public async Task Condition_selects_matching_branch_or_else()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [new FlowInputDto("score", "number", true, null, null)],
            [
                new FlowStepDto("c1", "condition", null, JsonNode.Parse("""
                    {"branches":[
                        {"when":{"left":"{{vars.score}}","op":"gte","right":80},
                         "steps":[{"id":"hit","type":"transform","name":null,"config":{"template":"high"},"steps":null}]},
                        {"when":{"left":"{{vars.score}}","op":"gte","right":50},
                         "steps":[{"id":"mid","type":"transform","name":null,"config":{"template":"mid"},"steps":null}]}
                    ],
                    "else":[{"id":"low","type":"transform","name":null,"config":{"template":"low"},"steps":null}]}
                    """)!.AsObject(), null),
            ]);

        var high = await engine.ExecuteAsync(def,
            new JsonObject { ["score"] = 90 }, Services(), null, null, CancellationToken.None);
        Assert.Contains(high.Steps, s => s.StepId == "hit" && s.Output?.GetValue<string>() == "high");

        var low = await engine.ExecuteAsync(def,
            new JsonObject { ["score"] = 10 }, Services(), null, null, CancellationToken.None);
        Assert.Contains(low.Steps, s => s.StepId == "low");
    }

    [Fact]
    public async Task Foreach_iterates_items_with_item_and_index_vars()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [],
            [
                new FlowStepDto("fe", "foreach", null, JsonNode.Parse("""
                    {"items":["a","b","c"],"as":"cur","indexAs":"i"}
                    """)!.AsObject(),
                    [new FlowStepDto("body", "transform", null,
                        JsonNode.Parse("""{"template":"{{vars.i}}:{{vars.cur}}"}""")!.AsObject(), null)]),
                Step("out", "output", new { value = "{{steps.fe.output}}" }),
            ]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("done", result.Status);
        var items = Assert.IsType<JsonArray>(result.Output);
        Assert.Equal(3, items.Count);
        Assert.Equal("0:a", items[0]!.GetValue<string>());
        Assert.Equal("2:c", items[2]!.GetValue<string>());
    }

    [Fact]
    public async Task Fail_step_aborts_run()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [],
            [
                Step("s1", "transform", new { template = "ok" }),
                Step("s2", "fail", new { message = "planned failure" }),
                Step("s3", "transform", new { template = "never" }),
            ]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("planned failure", result.Error);
        Assert.DoesNotContain(result.Steps, s => s.StepId == "s3");
    }

    [Fact]
    public async Task Unknown_step_type_fails_run()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto([], [Step("s1", "nonsense")]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("nonsense", result.Error);
    }

    [Fact]
    public async Task MaxSteps_cap_aborts()
    {
        var engine = NewEngine(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Flow:MaxSteps"] = "3" })
            .Build());
        var steps = Enumerable.Range(1, 5)
            .Select(i => Step($"s{i}", "transform", new { template = $"t{i}" })).ToList();
        var def = new FlowDefinitionDto([], steps);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.True(result.Steps.Count <= 3);
    }

    [Fact]
    public async Task ContinueOnError_records_error_and_continues()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [],
            [
                new FlowStepDto("boom", "fail", null,
                    JsonNode.Parse("""{"message":"x","continueOnError":true}""")!.AsObject(), null),
                Step("after", "transform", new { template = "reached" }),
            ]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Equal("failed", result.Steps[0].Status);
        Assert.Equal("done", result.Steps[1].Status);
    }

    [Fact]
    public async Task Tool_step_invokes_catalog_tool_with_templated_args()
    {
        string? seenQuery = null;
        var fake = new FakeCatalog(new CatalogTool
        {
            Name = "search_knowledge",
            Description = "fake",
            InputSchema = new JsonObject(),
            Handler = (ctx, _) =>
            {
                seenQuery = ctx.Arguments?["query"].GetString();
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "chunk-hit" }],
                });
            },
        });
        var sp = Services(s => s.AddSingleton<IDynamicToolCatalog>(fake));
        var engine = NewEngine();
        var def = new FlowDefinitionDto(
            [new FlowInputDto("q", "string", true, null, null)],
            [
                Step("k", "tool", new { tool = "search_knowledge", args = new { query = "{{vars.q}}" } }),
                Step("out", "output", new { value = "{{steps.k.output.text}}" }),
            ]);

        var result = await engine.ExecuteAsync(def,
            new JsonObject { ["q"] = "what is rrf" }, sp, null, null, CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Equal("what is rrf", seenQuery);
        Assert.Equal("chunk-hit", result.Output!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_step_unknown_tool_fails()
    {
        var sp = Services(s => s.AddSingleton<IDynamicToolCatalog>(new FakeCatalog()));
        var engine = NewEngine();
        var def = new FlowDefinitionDto([], [Step("k", "tool", new { tool = "ghost_tool" })]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), sp, null, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("ghost_tool", result.Error);
    }

    [Fact]
    public async Task Sink_receives_step_events_and_done()
    {
        var events = new List<string>();
        var engine = NewEngine();
        var def = new FlowDefinitionDto([], [Step("s1", "transform", new { template = "x" })]);

        await engine.ExecuteAsync(def, new JsonObject(), Services(),
            (ev, _) => { events.Add(ev.Type); return ValueTask.CompletedTask; }, null, CancellationToken.None);

        // Done/Error are emitted by the transport layer (FlowEndpoints), not the engine.
        Assert.Equal(
            new[] { FlowStreamEvent.StepStart, FlowStreamEvent.StepEnd },
            events);
    }

    [Fact]
    public async Task Retry_repeats_failed_step_until_success()
    {
        var calls = 0;
        var fake = new FakeCatalog(new CatalogTool
        {
            Name = "flaky_tool",
            Description = "fake",
            InputSchema = new JsonObject(),
            Handler = (_, _) =>
            {
                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("boom");
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "ok" }],
                });
            },
        });
        var sp = Services(s => s.AddSingleton<IDynamicToolCatalog>(fake));
        var engine = NewEngine();
        var def = new FlowDefinitionDto([],
            [Step("t", "tool", new { tool = "flaky_tool", retry = new { attempts = 3, backoffMs = 0 } })]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), sp, null, null, CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Equal(2, calls);
        Assert.Equal(2, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task Retry_exhausted_fails_with_attempt_count()
    {
        var calls = 0;
        var fake = new FakeCatalog(new CatalogTool
        {
            Name = "always_broken",
            Description = "fake",
            InputSchema = new JsonObject(),
            Handler = (_, _) => { calls++; throw new InvalidOperationException("still broken"); },
        });
        var sp = Services(s => s.AddSingleton<IDynamicToolCatalog>(fake));
        var engine = NewEngine();
        var def = new FlowDefinitionDto([],
            [Step("t", "tool", new { tool = "always_broken", retry = new { attempts = 3, backoffMs = 0 } })]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), sp, null, null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal(3, calls);
        Assert.Equal(3, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task Approval_step_suspends_run_with_resume_state()
    {
        var engine = NewEngine();
        var def = new FlowDefinitionDto([], [
            Step("before", "transform", new { template = "half-done" }),
            Step("gate", "approval", new { message = "ship it?" }),
            Step("after", "output", new { value = "shipped" }),
        ]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(), null, null, CancellationToken.None);

        Assert.Equal("waiting_approval", result.Status);
        Assert.NotNull(result.Pending);
        Assert.Equal("gate", result.Pending!.StepId);
        Assert.Equal("ship it?", result.Pending!.Message);
        Assert.NotNull(result.ResumeState);
        // 'after' never ran; 'before' + 'gate' are in the trace.
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("waiting", result.Steps[1].Status);
        Assert.DoesNotContain(result.Steps, s => s.StepId == "after");
    }

    [Fact]
    public async Task Resume_skips_executed_steps_and_continues()
    {
        var engine = NewEngine();
        var ran = new List<string>();
        var fake = new FakeCatalog(new CatalogTool
        {
            Name = "side_effect",
            Description = "fake",
            InputSchema = new JsonObject(),
            Handler = (_, _) =>
            {
                ran.Add("call");
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "did-it" }],
                });
            },
        });
        var sp = Services(s => s.AddSingleton<IDynamicToolCatalog>(fake));
        var def = new FlowDefinitionDto([], [
            Step("work", "tool", new { tool = "side_effect" }),
            Step("gate", "approval", new { message = "ok?" }),
            Step("out", "output", new { value = "{{steps.gate.output.resolution}}" }),
        ]);

        var first = await engine.ExecuteAsync(def, new JsonObject(), sp, null, null, CancellationToken.None);
        Assert.Equal("waiting_approval", first.Status);
        Assert.Single(ran); // side-effect ran once

        // Simulate FlowService: seed the gate's resolution and resume.
        var resume = first.ResumeState!;
        Assert.NotNull(resume.StepOutputs);
        resume.StepOutputs["gate"] = JsonNode.Parse("{\"resolution\":\"approved\"}");
        resume.PendingStepId = null;

        var second = await engine.ExecuteAsync(def, new JsonObject(), sp, null, resume, CancellationToken.None);

        Assert.Equal("done", second.Status);
        Assert.Single(ran); // no double side effects — 'work' was skipped
        Assert.Equal("approved", second.Output!.GetValue<string>());
    }

    [Fact]
    public async Task Resumed_run_emits_skip_events_for_restored_steps()
    {
        var engine = NewEngine();
        var events = new List<string>();
        var resume = new FlowResumeState
        {
            Vars = new JsonObject(),
            StepOutputs = new Dictionary<string, JsonNode?> { ["s1"] = "cached" },
            ExecutedCount = 1,
        };
        var def = new FlowDefinitionDto([], [
            Step("s1", "transform", new { template = "would-rerun" }),
            Step("s2", "output", new { value = "{{steps.s1.output}}" }),
        ]);

        var result = await engine.ExecuteAsync(def, new JsonObject(), Services(),
            (ev, _) => { events.Add(ev.Type); return ValueTask.CompletedTask; },
            resume, CancellationToken.None);

        Assert.Equal("done", result.Status);
        Assert.Equal("cached", result.Output!.GetValue<string>());
        Assert.Contains(FlowStreamEvent.StepSkip, events);
    }

    private sealed class FakeCatalog(params CatalogTool[] tools) : IDynamicToolCatalog
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
            IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>(tools);

        public Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(
            IServiceProvider services, CancellationToken cancellationToken) =>
            GetToolsAsync(services, cancellationToken);
    }
}
