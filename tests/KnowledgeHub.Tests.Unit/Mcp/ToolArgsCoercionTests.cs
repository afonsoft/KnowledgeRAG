using System.Text.Json;
using KnowledgeHub.Server.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace KnowledgeHub.Tests.Unit.Mcp;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-001: JSON-stringified argument
/// coercion in <see cref="ToolArgs"/> — LLM clients send typed args as strings.
/// </summary>
public class ToolArgsCoercionTests
{
    private static ToolCallContext Ctx(Dictionary<string, JsonElement> args) => new()
    {
        Services = new ServiceCollection().BuildServiceProvider(),
        Arguments = args
    };

    private static JsonElement J(string raw) =>
        JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement;

    [Fact]
    public void OptionalInt_StringifiedNumber_Coerces()
    {
        // Covers RF-001 AC-01: "5" for an int arg → 5.
        var ctx = Ctx(new() { ["topK"] = J("5") });
        Assert.Equal(5, ToolArgs.OptionalInt(ctx, "topK", 10, 50));
    }

    [Fact]
    public void OptionalInt_NonJsonString_ThrowsValidationError()
    {
        // Covers RF-001 AC-02: unparseable string → normal validation error.
        var ctx = Ctx(new() { ["topK"] = J("abc") });
        var ex = Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalInt(ctx, "topK", 10, 50));
        Assert.Contains("topK", ex.Message);
    }

    [Fact]
    public void OptionalBool_StringifiedBool_Coerces()
    {
        var ctx = Ctx(new() { ["confirm"] = J("true") });
        Assert.True(ToolArgs.OptionalBool(ctx, "confirm"));
    }

    [Fact]
    public void OptionalObject_StringifiedObject_Coerces()
    {
        // Covers RF-001: '{"start":"2026-01-01"}' → object.
        var ctx = Ctx(new() { ["temporalWindow"] = J("{\"start\":\"2026-01-01\"}") });
        var obj = ToolArgs.OptionalObject(ctx, "temporalWindow");
        Assert.NotNull(obj);
        Assert.Equal("2026-01-01", obj!.Value.GetProperty("start").GetString());
    }

    [Fact]
    public void OptionalObject_StringWithoutJson_ThrowsValidationError()
    {
        var ctx = Ctx(new() { ["temporalWindow"] = J("not-an-object") });
        Assert.Throws<McpProtocolException>(() => ToolArgs.OptionalObject(ctx, "temporalWindow"));
    }

    [Fact]
    public void OptionalStringArray_StringifiedArray_Coerces()
    {
        var ctx = Ctx(new() { ["tags"] = J("[\"a\",\"b\"]") });
        Assert.Equal(["a", "b"], ToolArgs.OptionalStringArray(ctx, "tags")!);
    }

    [Fact]
    public void OptionalString_NeverCoerced()
    {
        // String args keep their literal value — "true" as a query stays "true".
        var ctx = Ctx(new() { ["query"] = J("true") });
        Assert.Equal("true", ToolArgs.OptionalString(ctx, "query"));
    }
}
