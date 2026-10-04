using System.Text.Json;
using KnowledgeHub.Server.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers ToolArgs argument extraction helpers and ToolResults payload builders
// (SPEC-04 RF-002).
public class ToolArgsTests
{
    private static readonly IServiceProvider EmptyServices =
        new ServiceCollection().BuildServiceProvider();

    private static ToolCallContext Ctx(string json = "{}") => new()
    {
        Services = EmptyServices,
        Arguments = JsonDocument.Parse(json).RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
    };

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void RequiredString_ReturnsValue_WhenPresent()
    {
        var ctx = Ctx("""{"q":"hello"}""");
        Assert.Equal("hello", ToolArgs.RequiredString(ctx, "q"));
    }

    [Fact]
    public void RequiredString_Throws_WhenMissingOrEmpty()
    {
        var ctx = Ctx("""{"other":1,"empty":""}""");
        var ex = Assert.Throws<McpProtocolException>(() => ToolArgs.RequiredString(ctx, "q"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Throws<McpProtocolException>(() => ToolArgs.RequiredString(ctx, "empty"));
    }

    [Fact]
    public void RequiredString_Throws_WhenNotAString()
    {
        var ctx = Ctx("""{"q":42}""");
        Assert.Throws<McpProtocolException>(() => ToolArgs.RequiredString(ctx, "q"));
    }

    [Fact]
    public void OptionalString_ReturnsNull_WhenMissingOrNonString()
    {
        var ctx = Ctx("""{"n":5,"s":"x"}""");
        Assert.Null(ToolArgs.OptionalString(ctx, "missing"));
        Assert.Null(ToolArgs.OptionalString(ctx, "n"));
        Assert.Equal("x", ToolArgs.OptionalString(ctx, "s"));
    }

    [Fact]
    public void OptionalInt_ReturnsFallback_WhenMissingOrNonPositive()
    {
        var ctx = Ctx("""{"neg":-3,"zero":0,"big":999,"ok":7}""");
        Assert.Equal(10, ToolArgs.OptionalInt(ctx, "missing", fallback: 10, max: 50));
        Assert.Equal(10, ToolArgs.OptionalInt(ctx, "neg", fallback: 10, max: 50));
        Assert.Equal(10, ToolArgs.OptionalInt(ctx, "zero", fallback: 10, max: 50));
        Assert.Equal(50, ToolArgs.OptionalInt(ctx, "big", fallback: 10, max: 50));
        Assert.Equal(7, ToolArgs.OptionalInt(ctx, "ok", fallback: 10, max: 50));
    }

    [Fact]
    public void OptionalInt_Throws_WhenNotAnInteger()
    {
        var ctx = Ctx("""{"s":"x","f":1.5}""");
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalInt(ctx, "s", 1, 10));
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalInt(ctx, "f", 1, 10));
    }

    [Fact]
    public void OptionalIntOrNull_DistinguishesMissingFromZero()
    {
        var ctx = Ctx("""{"zero":0,"n":4}""");
        Assert.Null(ToolArgs.OptionalIntOrNull(ctx, "missing"));
        Assert.Equal(0, ToolArgs.OptionalIntOrNull(ctx, "zero"));
        Assert.Equal(4, ToolArgs.OptionalIntOrNull(ctx, "n"));
    }

    [Fact]
    public void OptionalIntOrNull_Throws_WhenNotAnInteger()
    {
        var ctx = Ctx("""{"b":true}""");
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalIntOrNull(ctx, "b"));
    }

    [Fact]
    public void OptionalBool_ParsesTrueFalse_NullOtherwise()
    {
        var ctx = Ctx("""{"t":true,"f":false,"s":"yes"}""");
        Assert.True(ToolArgs.OptionalBool(ctx, "t"));
        Assert.False(ToolArgs.OptionalBool(ctx, "f"));
        Assert.Null(ToolArgs.OptionalBool(ctx, "s"));
        Assert.Null(ToolArgs.OptionalBool(ctx, "missing"));
    }

    [Fact]
    public void OptionalObject_ReturnsElement_WhenObject()
    {
        var ctx = Ctx("""{"o":{"a":1},"s":"x"}""");
        Assert.Equal(JsonValueKind.Object, ToolArgs.OptionalObject(ctx, "o")!.Value.ValueKind);
        Assert.Null(ToolArgs.OptionalObject(ctx, "missing"));
    }

    [Fact]
    public void OptionalObject_Throws_WhenNotAnObject()
    {
        var ctx = Ctx("""{"s":"x"}""");
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalObject(ctx, "s"));
    }

    [Fact]
    public void OptionalScore_ReadsNestedNumber()
    {
        var obj = El("""{"min":0.75,"name":"x"}""");
        Assert.Equal(0.75, ToolArgs.OptionalScore(obj, "filter.min"));
        Assert.Null(ToolArgs.OptionalScore(obj, "filter.missing"));
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalScore(obj, "filter.name"));
    }

    [Fact]
    public void OptionalProp_ReadsNestedString()
    {
        var obj = El("""{"s":"v","n":1}""");
        Assert.Equal("v", ToolArgs.OptionalProp(obj, "o.s"));
        Assert.Null(ToolArgs.OptionalProp(obj, "o.missing"));
        Assert.Null(ToolArgs.OptionalProp(null, "o.s"));
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalProp(obj, "o.n"));
    }

    [Fact]
    public void OptionalStringArray_FiltersToStrings()
    {
        var ctx = Ctx("""{"a":["x",1,"y"],"n":3}""");
        var arr = ToolArgs.OptionalStringArray(ctx, "a");
        Assert.NotNull(arr);
        Assert.Equal(["x", "y"], arr);
        Assert.Null(ToolArgs.OptionalStringArray(ctx, "n"));
        Assert.Null(ToolArgs.OptionalStringArray(ctx, "missing"));
    }

    [Fact]
    public void NullArguments_TreatedAsMissing()
    {
        var ctx = new ToolCallContext { Services = EmptyServices, Arguments = null };
        Assert.Null(ToolArgs.OptionalString(ctx, "q"));
        Assert.Throws<McpProtocolException>(() => ToolArgs.RequiredString(ctx, "q"));
    }

    [Fact]
    public async Task ToolResults_Text_And_Error_Payloads()
    {
        var ok = await ToolResults.Text("done");
        Assert.False(ok.IsError);
        Assert.Equal("done", Assert.IsType<TextContentBlock>(ok.Content[0]).Text);

        var err = await ToolResults.Error("boom");
        Assert.True(err.IsError);
        Assert.Equal("boom", Assert.IsType<TextContentBlock>(err.Content[0]).Text);
    }

    [Fact]
    public async Task ToolResults_Structured_CarriesStructuredContent()
    {
        var result = await ToolResults.Structured("summary", new { count = 2 });
        Assert.False(result.IsError);
        Assert.Equal("summary", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("count").GetInt32());
    }
}
