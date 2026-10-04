using System.Text.Json.Nodes;
using KnowledgeHub.Client.Components;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Localization;

namespace KnowledgeHub.Tests.Unit.Client;

// FlowVisuals + FlowEditModel are the canvas/drawer view-models — pure logic
// over step JSON. Lanes must never throw on malformed raw JSON (the drawer
// keeps the text editable instead).
public class FlowVisualsTests
{
    private static FlowStepModel Step(string type, string config = "{}", string steps = "") =>
        new() { Id = "s1", Type = type, ConfigJson = config, StepsJson = steps };

    [Theory]
    [InlineData("tool", "fa-screwdriver-wrench")]
    [InlineData("knowledge", "fa-magnifying-glass")]
    [InlineData("llm", "fa-brain")]
    [InlineData("http", "fa-globe")]
    [InlineData("condition", "fa-code-branch")]
    [InlineData("foreach", "fa-repeat")]
    [InlineData("transform", "fa-wand-magic-sparkles")]
    [InlineData("output", "fa-flag-checkered")]
    [InlineData("fail", "fa-circle-xmark")]
    [InlineData("approval", "fa-user-check")]
    [InlineData("mystery", "fa-puzzle-piece")]
    public void TypeIcon_MapsEveryStepType(string type, string icon) =>
        Assert.Equal(icon, FlowVisuals.TypeIcon(type));

    [Fact]
    public void Summary_ToolStep_ReturnsToolName()
    {
        var summary = FlowVisuals.Summary(Step("tool", """{"tool":"search_knowledge"}"""));
        Assert.Equal("search_knowledge", summary);
    }

    [Fact]
    public void Summary_HttpStep_ReturnsMethodAndUrl()
    {
        var summary = FlowVisuals.Summary(Step("http", """{"method":"POST","url":"https://a.b/x"}"""));
        Assert.Equal("POST https://a.b/x", summary);
    }

    [Fact]
    public void Summary_HttpStepWithoutMethod_DefaultsToGet()
    {
        var summary = FlowVisuals.Summary(Step("http", """{"url":"https://a.b"}"""));
        Assert.Equal("GET https://a.b", summary);
    }

    [Fact]
    public void Summary_LlmStep_TruncatesLongPrompt()
    {
        var prompt = new string('p', 80);
        var summary = FlowVisuals.Summary(Step("llm", $$"""{"prompt":"{{prompt}}"}"""));
        Assert.Equal(61, summary!.Length); // 60 chars + ellipsis
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void Summary_ForeachStep_ShowsEachExpression()
    {
        var summary = FlowVisuals.Summary(Step("foreach", """{"each":"vars.items"}"""));
        Assert.EndsWith("\"vars.items\"", summary);
    }

    [Theory]
    [InlineData("transform", "reshape")]
    [InlineData("fail", "boom")]
    [InlineData("approval", "ok?")]
    public void Summary_StaticLabels(string type, string expected)
    {
        var config = type switch
        {
            "fail" or "approval" => $$"""{"message":"{{expected}}"}""",
            _ => "{}",
        };
        Assert.Equal(expected, FlowVisuals.Summary(Step(type, config)));
    }

    [Fact]
    public void Summary_OutputStep_RendersValueJson()
    {
        var summary = FlowVisuals.Summary(Step("output", """{"value":{"a":1}}"""));
        Assert.Equal("""{"a":1}""", summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    public void Summary_InvalidOrBlankConfig_ReturnsNull(string config)
    {
        Assert.Null(FlowVisuals.Summary(Step("tool", config)));
        Assert.Empty(FlowVisuals.DisplayLanes(Step("condition", config)));
    }

    [Fact]
    public void Summary_UnknownType_ReturnsNull() =>
        Assert.Null(FlowVisuals.Summary(Step("mystery", """{"x":1}""")));

    [Fact]
    public void DisplayLanes_ForeachModel_ParsesNestedSteps()
    {
        var lanes = FlowVisuals.DisplayLanes(
            Step("foreach", "{}", """[{"id":"n1","type":"tool"},{"id":"n2","type":"http"}]"""));

        var lane = Assert.Single(lanes);
        Assert.Equal("loop", lane.Label);
        Assert.Equal(2, lane.Steps.Count);
        Assert.Equal("n2", lane.Steps[1].Id);
    }

    [Fact]
    public void DisplayLanes_ForeachModel_MalformedStepsJson_YieldsEmptyLane()
    {
        var lane = Assert.Single(FlowVisuals.DisplayLanes(Step("foreach", "{}", "{bad")));
        Assert.Equal("loop", lane.Label);
        Assert.Empty(lane.Steps);
    }

    [Fact]
    public void DisplayLanes_ConditionModel_ReadsBranchesAndElse()
    {
        var config = """
            {"branches":[
                {"when":{"left":"vars.x","op":"eq","right":1},"steps":[{"id":"a","type":"tool"}]},
                {"steps":[{"id":"b","type":"tool"}]}
            ],
            "else":[{"id":"c","type":"fail"}]}
            """;

        var lanes = FlowVisuals.DisplayLanes(Step("condition", config));

        Assert.Equal(3, lanes.Count);
        Assert.Equal("vars.x eq 1", lanes[0].Label);
        Assert.Equal("a", lanes[0].Steps[0].Id);
        Assert.NotEmpty(lanes[1].Label); // no `when` → localized "always"
        Assert.Equal("else", lanes[2].Label);
        Assert.Equal("c", lanes[2].Steps[0].Id);
    }

    [Fact]
    public void DisplayLanes_WhenWithoutRight_RendersTwoParts()
    {
        var config = """{"branches":[{"when":{"left":"vars.x","op":"exists"},"steps":[]}]}""";
        var lane = Assert.Single(FlowVisuals.DisplayLanes(Step("condition", config)));
        Assert.Equal("vars.x exists", lane.Label);
    }

    [Fact]
    public void DisplayLanes_ToolModel_HasNoLanes() =>
        Assert.Empty(FlowVisuals.DisplayLanes(Step("tool", """{"tool":"t"}""")));

    [Fact]
    public void DisplayLanes_ForeachDto_ReturnsNestedSteps()
    {
        var dto = new FlowStepDto("s1", "foreach", null, null,
            [new FlowStepDto("n1", "tool", null, null, null)]);

        var lane = Assert.Single(FlowVisuals.DisplayLanes(dto));
        Assert.Equal("loop", lane.Label);
        Assert.Equal("n1", lane.Steps[0].Id);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"tool":"search_knowledge"}""")]
    public void ConfigHasError_ValidOrBlankConfig_ReturnsFalse(string config) =>
        Assert.False(FlowVisuals.ConfigHasError(Step("tool", config)));

    [Theory]
    [InlineData("{bad")]
    [InlineData("not json")]
    [InlineData("[1,2]")] // parses but is not an object — same rule as save
    [InlineData("\"text\"")]
    public void ConfigHasError_MalformedOrNonObjectConfig_ReturnsTrue(string config) =>
        Assert.True(FlowVisuals.ConfigHasError(Step("tool", config)));

    [Fact]
    public void ConfigHasError_MalformedStepsJson_ReturnsTrue() =>
        Assert.True(FlowVisuals.ConfigHasError(Step("foreach", "{}", "{bad")));

    [Fact]
    public void ConfigHasError_ValidStepsJson_ReturnsFalse() =>
        Assert.False(FlowVisuals.ConfigHasError(
            Step("foreach", "{}", """[{"id":"n1","type":"tool"}]""")));

    [Fact]
    public void DisplayLanes_ConditionDto_ReadsConfigBranches()
    {
        var config = JsonNode.Parse("""{"else":[{"id":"e","type":"fail"}]}""")!.AsObject();
        var dto = new FlowStepDto("s1", "condition", null, config, null);

        var lane = Assert.Single(FlowVisuals.DisplayLanes(dto));
        Assert.Equal("else", lane.Label);
        Assert.Equal("e", lane.Steps[0].Id);
    }
}

public class FlowEditModelTests
{
    private sealed class EchoLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    [Fact]
    public void Empty_HasStarterStepAndJson()
    {
        var model = FlowEditModel.Empty();
        Assert.StartsWith("{", model.DefinitionJson);
        Assert.Contains("search_knowledge", model.DefinitionJson);
        Assert.True(model.Enabled);
        Assert.Equal(FlowEditorView.Canvas, model.View); // canvas-first editor
    }

    [Fact]
    public void StepTypeItems_CoversAllTypes()
    {
        var items = FlowEditModel.StepTypeItems(new EchoLocalizer());
        Assert.Equal(
            ["tool", "knowledge", "llm", "http", "condition", "foreach", "transform", "output", "fail", "approval"],
            items.Select(i => i.Value).ToArray());
    }

    [Fact]
    public void From_MapsDetailIntoEditableModel()
    {
        var detail = new FlowDetailDto(
            new FlowDto(Guid.NewGuid(), "Echo", "echo", "desc", false, 3, DateTimeOffset.UtcNow),
            new FlowDefinitionDto(
                [new FlowInputDto("q", "string", true, "question", JsonValue.Create("default"))],
                [
                    new FlowStepDto("s1", "tool", "Search", JsonNode.Parse("""{"tool":"search_knowledge"}""")!.AsObject(), null),
                    new FlowStepDto("s2", "foreach", null, null,
                        [new FlowStepDto("n1", "transform", null, null, null)]),
                ]));

        var model = FlowEditModel.From(detail);

        Assert.Equal("Echo", model.Name);
        Assert.False(model.Enabled);

        var input = Assert.Single(model.Inputs);
        Assert.Equal("q", input.Name);
        Assert.True(input.Required);
        Assert.Equal("\"default\"", input.DefaultJson);

        Assert.Equal(2, model.Steps.Count);
        Assert.Equal("""{"tool":"search_knowledge"}""", model.Steps[0].ConfigJson);
        Assert.Contains("n1", model.Steps[1].StepsJson);
        Assert.Empty(model.Steps[0].StepsJson); // no nested steps → blank
    }
}
