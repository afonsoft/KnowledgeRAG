using System.Text.Json;
using KnowledgeHub.Shared.Tooling;

namespace KnowledgeHub.Tests.Unit.Shared;

// Covers SPEC-20260914-playground-tool-form RF-001..RF-008.
public class ToolArgumentBuilderTests
{
    private static readonly JsonElement AskQuestionSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repoName":{"anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"},"maxItems":10}],
                      "description":"GitHub repo(s) in owner/repo format (max 10)",
                      "examples":["langchain-ai/langgraph"]},
          "question":{"type":"string","description":"Question about the repository"}
        },"required":["repoName","question"],
        "examples":[{"repoName":"langchain-ai/langgraph","question":"How does checkpointing work?"}]}
        """).RootElement.Clone();

    private static readonly JsonElement SearchSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Texto ou pergunta a buscar"},
          "topK":{"type":"integer","description":"Máx. de resultados"},
          "mode":{"type":"string","enum":["hybrid","semantic","lexical"]}
        },"required":["query"]}
        """).RootElement.Clone();

    private static readonly JsonElement WriteSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string"},
          "content":{"type":"string"},
          "tags":{"type":"array","items":{"type":"string"}}
        },"required":["title","content"]}
        """).RootElement.Clone();

    private static ToolField Field(JsonElement schema, string name)
        => ToolArgumentBuilder.ParseFields(schema).Single(f => f.Name == name);

    // --- RF-002: field parsing -------------------------------------------

    [Fact]
    public void ParseFields_AnyOfStringOrStringArray_ResolvesStringOrStringList()
    {
        var f = Field(AskQuestionSchema, "repoName");
        Assert.Equal(ToolFieldKind.StringOrStringList, f.Kind);
        Assert.Equal("string | string[]", f.TypeLabel);
        Assert.Equal(10, f.MaxItems);
        Assert.True(f.Required);
        Assert.True(f.Simple); // text input, not JSON textarea
        Assert.Equal("langchain-ai/langgraph", f.Placeholder); // property examples[0]
    }

    [Fact]
    public void ParseFields_Enum_ResolvesChoice()
    {
        var f = Field(SearchSchema, "mode");
        Assert.Equal(ToolFieldKind.Choice, f.Kind);
        Assert.Equal(["hybrid", "semantic", "lexical"], f.EnumValues);
        Assert.False(f.Required);
    }

    [Fact]
    public void ParseFields_ArrayOfStrings_ResolvesStringList()
    {
        var f = Field(WriteSchema, "tags");
        Assert.Equal(ToolFieldKind.StringList, f.Kind);
        Assert.False(f.Required);
    }

    [Fact]
    public void ParseFields_NullableUnion_TreatedAsOptionalSimpleType()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "maybe":{"anyOf":[{"type":"string"},{"type":"null"}]}
            },"required":["maybe"]}
            """).RootElement;
        var f = Field(schema, "maybe");
        Assert.Equal(ToolFieldKind.String, f.Kind);
        Assert.False(f.Required); // T | null → optional
    }

    // --- RF-003: coercion -------------------------------------------------

    [Fact]
    public void TryBuild_AnyOf_BareString_BecomesString()
    {
        var f = Field(AskQuestionSchema, "repoName");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "langchain-ai/langgraph", out var v, out var err));
        Assert.Null(err);
        Assert.Equal(JsonValueKind.String, v.ValueKind);
        Assert.Equal("langchain-ai/langgraph", v.GetString());
    }

    [Fact]
    public void TryBuild_AnyOf_CommaList_BecomesArray()
    {
        var f = Field(AskQuestionSchema, "repoName");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "a/b, c/d ,, e/f ", out var v, out _));
        Assert.Equal(JsonValueKind.Array, v.ValueKind);
        Assert.Equal("a/b,c/d,e/f", string.Join(',', v.EnumerateArray().Select(e => e.GetString())));
    }

    [Fact]
    public void TryBuild_AnyOf_MaxItemsOverflow_Fails()
    {
        var f = Field(AskQuestionSchema, "repoName");
        var raw = string.Join(", ", Enumerable.Range(0, 11).Select(i => $"o{i}/r{i}"));
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, raw, out _, out var err));
        Assert.Contains("10", err);
    }

    [Fact]
    public void TryBuild_StringList_AlwaysArray()
    {
        var f = Field(WriteSchema, "tags");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "a, b", out var v, out _));
        Assert.Equal("a,b", string.Join(',', v.EnumerateArray().Select(e => e.GetString())));
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "single", out var one, out _));
        Assert.Equal("single", string.Join(',', one.EnumerateArray().Select(e => e.GetString())));
    }

    [Fact]
    public void TryBuild_EmptyOptional_Omits()
    {
        var f = Field(SearchSchema, "topK");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "   ", out var v, out var err));
        Assert.Null(err);
        Assert.Equal(JsonValueKind.Undefined, v.ValueKind); // omit
    }

    [Fact]
    public void TryBuild_NullableUnion_EmptyOmits_NonEmptyTyped()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"maybe":{"anyOf":[{"type":"integer"},{"type":"null"}]}}}
            """).RootElement;
        var f = Field(schema, "maybe");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "", out var omit, out _));
        Assert.Equal(JsonValueKind.Undefined, omit.ValueKind);
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "42", out var v, out _));
        Assert.Equal(42, v.GetInt32());
    }

    [Fact]
    public void TryBuild_Choice_RejectsOutsideEnum()
    {
        var f = Field(SearchSchema, "mode");
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, "bogus", out _, out var err));
        Assert.Contains("hybrid", err);
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "semantic", out var v, out _));
        Assert.Equal("semantic", v.GetString());
    }

    [Fact]
    public void TryBuild_JsonUnion_NonStringMembers_ValidateKind()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "arg":{"anyOf":[{"type":"object"},{"type":"integer"}]}
            }}
            """).RootElement;
        var f = Field(schema, "arg");
        Assert.Equal(ToolFieldKind.Json, f.Kind);
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, "!!", out _, out var err));
        Assert.Contains("object", err);
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "5", out var v, out _));
        Assert.Equal(JsonValueKind.Number, v.ValueKind);
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "{\"a\":1}", out var o, out _));
        Assert.Equal(JsonValueKind.Object, o.ValueKind);
    }

    [Fact]
    public void TryBuild_JsonUnion_WithStringMember_CoercesBareText()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "arg":{"anyOf":[{"type":"string"},{"type":"object"}]}
            }}
            """).RootElement;
        var f = Field(schema, "arg");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "not json {", out var v, out _));
        Assert.Equal(JsonValueKind.String, v.ValueKind);
        Assert.Equal("not json {", v.GetString());
    }

    // --- RF-006/RF-007: examples + skeleton --------------------------------

    [Fact]
    public void GetExamples_ReturnsRootExamples()
    {
        var examples = ToolArgumentBuilder.GetExamples(AskQuestionSchema);
        Assert.Single(examples);
        Assert.Equal("langchain-ai/langgraph", examples[0].GetProperty("repoName").GetString());
    }

    [Fact]
    public void FillFromArguments_WritesRawTextPerFieldKind()
    {
        var fields = ToolArgumentBuilder.ParseFields(AskQuestionSchema);
        var example = ToolArgumentBuilder.GetExamples(AskQuestionSchema)[0];
        ToolArgumentBuilder.FillFromArguments(fields, example);
        Assert.Equal("langchain-ai/langgraph", fields.Single(f => f.Name == "repoName").TextValue);
        Assert.Equal("How does checkpointing work?", fields.Single(f => f.Name == "question").TextValue);
    }

    [Fact]
    public void FillFromArguments_StringList_JoinsWithCommas()
    {
        var fields = ToolArgumentBuilder.ParseFields(WriteSchema);
        var args = JsonDocument.Parse("""{"title":"t","content":"c","tags":["x","y"]}""").RootElement;
        ToolArgumentBuilder.FillFromArguments(fields, args);
        Assert.Equal("x, y", fields.Single(f => f.Name == "tags").TextValue);
    }

    [Fact]
    public void FillSkeleton_FillsRequiredFieldsOnly()
    {
        var fields = ToolArgumentBuilder.ParseFields(SearchSchema);
        ToolArgumentBuilder.FillSkeleton(fields);
        Assert.False(string.IsNullOrWhiteSpace(fields.Single(f => f.Name == "query").TextValue));
        Assert.True(string.IsNullOrWhiteSpace(fields.Single(f => f.Name == "topK").TextValue));
        Assert.True(string.IsNullOrWhiteSpace(fields.Single(f => f.Name == "mode").TextValue));
    }

    // --- RF-003 edge branches --------------------------------------------

    [Fact]
    public void TryBuild_String_Passthrough()
    {
        var f = Field(SearchSchema, "query");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "  spaced value  ", out var v, out var err));
        Assert.Null(err);
        Assert.Equal("  spaced value  ", v.GetString());
    }

    [Fact]
    public void TryBuild_Number_RejectsNonNumeric()
    {
        var f = Field(SearchSchema, "topK");
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, "abc", out _, out var err));
        Assert.Contains("integer", err);
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "3.5", out var v, out _));
        Assert.Equal(3.5, v.GetDouble());
    }

    [Fact]
    public void TryBuild_Boolean_AcceptsTrueFalse_RejectsOther()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"flag":{"type":"boolean"}}}
            """).RootElement;
        var f = Field(schema, "flag");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, " true ", out var v, out _));
        Assert.True(v.GetBoolean());
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, "yes", out _, out var err));
        Assert.Contains("boolean", err);
    }

    [Fact]
    public void TryBuild_EmptyListInput_Omits()
    {
        var f = Field(WriteSchema, "tags");
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, " , ,", out var v, out var err));
        Assert.Null(err);
        Assert.Equal(JsonValueKind.Undefined, v.ValueKind);
    }

    [Fact]
    public void TryBuild_UnknownKind_ReportsUnsupported()
    {
        var f = new ToolField
        {
            Name = "x",
            Kind = (ToolFieldKind)99,
            TypeLabel = "mystery"
        };
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, "anything", out _, out var err));
        Assert.Contains("mystery", err);
    }

    // --- RF-001 edge branches --------------------------------------------

    [Fact]
    public void ParseFields_NonObjectOrMissingProperties_Empty()
    {
        Assert.Empty(ToolArgumentBuilder.ParseFields(JsonDocument.Parse("[]").RootElement));
        Assert.Empty(ToolArgumentBuilder.ParseFields(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(ToolArgumentBuilder.ParseFields(
            JsonDocument.Parse("""{"properties":"nope"}""").RootElement));
    }

    [Fact]
    public void ParseFields_EmptyEnumArray_FallsThroughToKind()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"m":{"type":"string","enum":[]}}}
            """).RootElement;
        var f = Field(schema, "m");
        Assert.Equal(ToolFieldKind.String, f.Kind); // empty enum is not a Choice
    }

    [Fact]
    public void ParseFields_UnknownType_ResolvesJson()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"blob":{"type":"bytes"}}}
            """).RootElement;
        var f = Field(schema, "blob");
        Assert.Equal(ToolFieldKind.Json, f.Kind);
        Assert.False(f.Simple);
    }

    [Fact]
    public void ParseFields_DefaultValue_BecomesPlaceholder()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"n":{"type":"integer","default":7}}}
            """).RootElement;
        Assert.Equal("7", Field(schema, "n").Placeholder);
    }

    [Fact]
    public void ParseFields_UnionWithObjectMember_ResolvesJsonWithKindCheck()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "cfg":{"anyOf":[{"type":"object"},{"type":"boolean"}]}
            }}
            """).RootElement;
        var f = Field(schema, "cfg");
        Assert.Equal(ToolFieldKind.Json, f.Kind);
        Assert.False(ToolArgumentBuilder.TryBuildArgument(f, "\"str\"", out _, out _));
        Assert.True(ToolArgumentBuilder.TryBuildArgument(f, "true", out var v, out _));
        Assert.Equal(JsonValueKind.True, v.ValueKind);
    }

    // --- RF-006/RF-007 edge branches -------------------------------------

    [Fact]
    public void GetExamples_NonArrayOrAbsent_Empty()
    {
        Assert.Empty(ToolArgumentBuilder.GetExamples(
            JsonDocument.Parse("""{"examples":"x"}""").RootElement));
        Assert.Empty(ToolArgumentBuilder.GetExamples(
            JsonDocument.Parse("""{"type":"object"}""").RootElement));
    }

    [Fact]
    public void FillFromArguments_NonObjectArgs_IsNoOp()
    {
        var fields = ToolArgumentBuilder.ParseFields(WriteSchema);
        ToolArgumentBuilder.FillFromArguments(
            fields, JsonDocument.Parse("[1,2]").RootElement);
        Assert.All(fields, f => Assert.Null(f.TextValue));
    }

    [Fact]
    public void FillFromArguments_UnknownField_Ignored()
    {
        var fields = ToolArgumentBuilder.ParseFields(WriteSchema);
        var args = JsonDocument.Parse("""{"ghost":"x","n":1}""").RootElement;
        ToolArgumentBuilder.FillFromArguments(fields, args);
        Assert.All(fields, f => Assert.Null(f.TextValue));
    }

    [Fact]
    public void FillFromArguments_NumberField_RawText()
    {
        var fields = ToolArgumentBuilder.ParseFields(SearchSchema);
        var args = JsonDocument.Parse("""{"query":"q","topK":9}""").RootElement;
        ToolArgumentBuilder.FillFromArguments(fields, args);
        Assert.Equal("9", fields.Single(f => f.Name == "topK").TextValue);
    }

    [Fact]
    public void FillSkeleton_RespectsExistingTextValue()
    {
        var fields = ToolArgumentBuilder.ParseFields(SearchSchema);
        fields.Single(f => f.Name == "query").TextValue = "preset";
        ToolArgumentBuilder.FillSkeleton(fields);
        Assert.Equal("preset", fields.Single(f => f.Name == "query").TextValue);
    }
}
