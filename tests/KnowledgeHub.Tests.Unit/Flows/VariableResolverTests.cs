using System.Text.Json.Nodes;
using KnowledgeHub.Server.Flows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Flows;

/// <summary><c>{{path}}</c> templating: interpolation, typed single-expr,
/// path traversal (dot / [n] / ["k"]), vars + steps.* sources.</summary>
public sealed class VariableResolverTests
{
    private static FlowExecContext Ctx(Action<FlowExecContext>? configure = null)
    {
        var ctx = new FlowExecContext
        {
            Services = new ServiceCollection().BuildServiceProvider(),
            Limits = new FlowLimits(),
        };
        configure?.Invoke(ctx);
        return ctx;
    }

    [Fact]
    public void ResolveString_embeds_vars_and_leaves_unknown_empty()
    {
        var ctx = Ctx(c =>
        {
            c.Vars["name"] = "obsidian";
            c.Vars["n"] = 3;
        });

        var result = VariableResolver.ResolveString("vault {{vars.name}} x{{vars.n}} {{vars.missing}}", ctx);

        Assert.Equal("vault obsidian x3 ", result);
    }

    [Fact]
    public void ResolveNode_single_expression_returns_typed_node()
    {
        var ctx = Ctx(c =>
        {
            c.Vars["arr"] = new JsonArray(1, 2, 3);
            c.Vars["count"] = 42;
        });

        var arr = VariableResolver.ResolveNode(JsonValue.Create("{{vars.arr}}"), ctx);
        Assert.IsType<JsonArray>(arr);
        Assert.Equal(3, arr!.AsArray().Count);

        var n = VariableResolver.ResolveNode(JsonValue.Create("{{vars.count}}"), ctx);
        Assert.Equal(42, n!.GetValue<int>());
    }

    [Fact]
    public void ResolvePath_traverses_object_index_and_quoted_keys()
    {
        var ctx = Ctx(c =>
            c.StepOutputs["s1"] = JsonNode.Parse("""{"items":[{"name":"a"},{"name":"b"}],"meta":{"k-1":"v"}}"""));

        Assert.Equal("b", VariableResolver.ResolvePath("steps.s1.output.items[1].name", ctx)!.GetValue<string>());
        Assert.Equal("v", VariableResolver.ResolvePath("""steps.s1.output.meta["k-1"]""", ctx)!.GetValue<string>());
        Assert.Null(VariableResolver.ResolvePath("steps.s1.output.items[5].name", ctx));
    }

    [Fact]
    public void ResolvePath_bare_name_falls_back_to_vars()
    {
        var ctx = Ctx(c => c.Vars["q"] = "hello");
        Assert.Equal("hello", VariableResolver.ResolvePath("q", ctx)!.GetValue<string>());
    }

    [Fact]
    public void ResolvePath_reads_step_error()
    {
        var ctx = Ctx(c => c.StepErrors["s1"] = "boom");
        Assert.Equal("boom", VariableResolver.ResolvePath("steps.s1.error", ctx)!.GetValue<string>());
    }

    [Fact]
    public void ParseSegments_handles_mixed_syntax()
    {
        var segs = VariableResolver.ParseSegments("""a.b[2]["x-y"].c""");
        Assert.Equal(new[] { "a", "b", "2", "x-y", "c" }, segs);
    }
}
