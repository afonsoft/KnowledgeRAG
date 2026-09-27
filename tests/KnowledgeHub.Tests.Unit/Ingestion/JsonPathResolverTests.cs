using System.Text.Json;
using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-restapi-sqldatabase-connectors RF-002: dot-path
// navigation over JSON objects with numeric segments indexing arrays.
public class JsonPathResolverTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void NestedObjectPath_Resolves()
    {
        var root = El("""{"data":{"results":{"id":7}}}""");
        var resolved = JsonPathResolver.Resolve(root, "data.results.id");
        Assert.NotNull(resolved);
        Assert.Equal(7, resolved!.Value.GetInt32());
    }

    [Fact]
    public void NumericSegment_IndexesArray()
    {
        var root = El("""{"items":[{"id":"a"},{"id":"b"}]}""");
        var resolved = JsonPathResolver.Resolve(root, "items.1.id");
        Assert.NotNull(resolved);
        Assert.Equal("b", resolved!.Value.GetString());
    }

    [Fact]
    public void EmptyPath_ReturnsRoot()
    {
        var root = El("""{"a":1}""");
        var resolved = JsonPathResolver.Resolve(root, "");
        Assert.NotNull(resolved);
        Assert.Equal("""{"a":1}""", resolved!.Value.GetRawText());
    }

    [Fact]
    public void MissingProperty_ReturnsNull()
    {
        var root = El("""{"data":{}}""");
        Assert.Null(JsonPathResolver.Resolve(root, "data.missing.deep"));
    }

    [Fact]
    public void ArrayIndexOutOfRange_ReturnsNull()
    {
        var root = El("""{"items":[1,2]}""");
        Assert.Null(JsonPathResolver.Resolve(root, "items.5"));
    }

    [Fact]
    public void NonNumericSegmentOnArray_ReturnsNull()
    {
        var root = El("""{"items":[1,2]}""");
        Assert.Null(JsonPathResolver.Resolve(root, "items.first"));
    }

    [Fact]
    public void SegmentOnScalar_ReturnsNull()
    {
        var root = El("""{"value":42}""");
        Assert.Null(JsonPathResolver.Resolve(root, "value.deeper"));
    }

    [Fact]
    public void NullElement_ReturnsNull()
    {
        var root = El("""{"a":null}""");
        Assert.Null(JsonPathResolver.Resolve(root, "a.b"));
    }

    [Fact]
    public void WhitespaceSegments_AreTrimmed()
    {
        var root = El("""{"a":{"b":1}}""");
        var resolved = JsonPathResolver.Resolve(root, " a . b ");
        Assert.NotNull(resolved);
        Assert.Equal(1, resolved!.Value.GetInt32());
    }
}
