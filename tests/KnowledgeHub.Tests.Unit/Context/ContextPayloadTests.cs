using System.Text.Json;
using KnowledgeHub.Server.Context;
using ModelContextProtocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Context;

/// <summary>SPEC-20261004-context-snapshots RF-003 — payload contract.</summary>
public class ContextPayloadTests
{
    private static JsonElement Items(object items) =>
        JsonSerializer.SerializeToElement(new { items }).GetProperty("items");

    [Fact]
    public void ParseItems_ValidPayload_Roundtrips()
    {
        var items = ContextPayload.ParseItems(Items(new object[]
        {
            new { kind = "decision", title = "t", content = "c", score = 0.5 },
            new { kind = "tool_output", tool = "search_knowledge", content = "out" }
        }), 10);
        Assert.Equal(2, items.Count);
        Assert.Equal("decision", items[0].Kind);
        Assert.Equal("search_knowledge", items[1].Tool);
    }

    [Fact]
    public void ParseItems_UnknownField_Rejected()
    {
        var ex = Assert.Throws<McpProtocolException>(() =>
            ContextPayload.ParseItems(Items(new[] { new { kind = "note", content = "c", bogus = 1 } }), 10));
        Assert.Contains("invalid_payload", ex.Message);
        Assert.Contains("unknown field", ex.Message);
    }

    [Fact]
    public void ParseItems_UnknownKind_Rejected()
    {
        var ex = Assert.Throws<McpProtocolException>(() =>
            ContextPayload.ParseItems(Items(new[] { new { kind = "evil", content = "c" } }), 10));
        Assert.Contains("invalid_payload", ex.Message);
    }

    [Theory]
    [InlineData("sk-1234567890")]
    [InlineData("token aft_abc123")]
    [InlineData("ghp_abcdefghij")]
    [InlineData("ctx7sk-xyz")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("AIzaSyA123")]
    public void ParseItems_SecretMarkers_Rejected(string content)
    {
        var ex = Assert.Throws<McpProtocolException>(() =>
            ContextPayload.ParseItems(Items(new[] { new { kind = "note", content } }), 10));
        Assert.Contains("secret_detected", ex.Message);
    }

    [Fact]
    public void ParseItems_ControlChars_Rejected()
    {
        var ex = Assert.Throws<McpProtocolException>(() =>
            ContextPayload.ParseItems(Items(new[] { new { kind = "note", content = "a\u0007b" } }), 10));
        Assert.Contains("control characters", ex.Message);
    }

    [Fact]
    public void ParseItems_NewlineAndTab_Allowed()
    {
        var items = ContextPayload.ParseItems(
            Items(new[] { new { kind = "note", content = "line1\nline2\ttab" } }), 10);
        Assert.Single(items);
    }

    [Fact]
    public void ParseItems_OverMaxItems_ThrowsPayloadTooLarge()
    {
        var arr = Enumerable.Range(0, 5).Select(i => new { kind = "note", content = $"c{i}" }).ToArray();
        var ex = Assert.Throws<McpProtocolException>(() =>
            ContextPayload.ParseItems(Items(arr), maxItems: 3));
        Assert.Contains("payload_too_large", ex.Message);
    }

    [Fact]
    public void ParseItems_ContentOverLimit_Rejected()
    {
        var big = new string('x', 16_001);
        var ex = Assert.Throws<McpProtocolException>(() =>
            ContextPayload.ParseItems(Items(new[] { new { kind = "note", content = big } }), 10));
        Assert.Contains("invalid_payload", ex.Message);
    }

    [Fact]
    public void ParseItems_NonArray_Rejected()
    {
        var el = JsonSerializer.SerializeToElement(new { items = "nope" }).GetProperty("items");
        var ex = Assert.Throws<McpProtocolException>(() => ContextPayload.ParseItems(el, 10));
        Assert.Contains("invalid_payload", ex.Message);
    }

    [Fact]
    public void Serialize_Deserialize_Roundtrip()
    {
        var items = new List<ContextItem>
        {
            new() { Kind = "decision", Title = "t", Content = "c" },
            new() { Kind = "passage", Content = "p", Score = 0.9, Path = "a.md" }
        };
        var json = ContextPayload.Serialize(items);
        var back = ContextPayload.Deserialize(json);
        Assert.Equal(2, back.Items.Count);
        Assert.Equal("a.md", back.Items[1].Path);
    }

    [Fact]
    public void EstimateTokens_CeilDiv4()
    {
        Assert.Equal(3, ContextPayload.EstimateTokens("123456789"));
        Assert.Equal(0, ContextPayload.EstimateTokens(""));
    }
}
