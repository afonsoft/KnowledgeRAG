using KnowledgeHub.Server.Agent;
using KnowledgeHub.Server.Domain.Entities;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Agent;

// SPEC-20261021-model-capability-profiles RF-6: pattern matching, merge,
// and budget derivation rules.
public class ModelProfilesTests
{
    [Theory]
    [InlineData("gpt-4o*", "gpt-4o-mini", true)]
    [InlineData("gpt-4o*", "GPT-4O", true)] // case-insensitive
    [InlineData("gpt-4o", "gpt-4o", true)] // exact
    [InlineData("gpt-4o", "gpt-4o-mini", false)] // exact does not prefix-match
    [InlineData("*", "anything", true)]
    public void Matches_Patterns_Behave(string pattern, string model, bool expected) =>
        Assert.Equal(expected, ModelProfiles.Matches(pattern, model));

    [Fact]
    public void Resolve_PrefersMostSpecificPrefix()
    {
        var resolved = ModelProfiles.Resolve("gpt-4o-mini", ModelProfiles.Builtin);
        Assert.Equal("gpt-4o*", resolved!.ModelPattern);
        Assert.True(resolved.SupportsToolCalls);
    }

    [Fact]
    public void Resolve_FallsBackToCatchAll()
    {
        var resolved = ModelProfiles.Resolve("totally-unknown-model", ModelProfiles.Builtin);
        Assert.Equal("*", resolved!.ModelPattern);
    }

    [Fact]
    public void Merge_UserOverride_WinsOnSamePattern()
    {
        var merged = ModelProfiles.Merge([new ModelProfile
        {
            ModelPattern = "llama3*", Source = "user",
            SupportsToolCalls = false, ContextWindowTokens = 8192
        }]);
        var resolved = ModelProfiles.Resolve("llama3.2:1b", merged)!;
        Assert.Equal("user", resolved.Source);
        Assert.False(resolved.SupportsToolCalls);
        Assert.Equal(8192, resolved.ContextWindowTokens);
    }

    [Fact]
    public void ContextBudgetBytes_CapsAtProfileWindow()
    {
        var profile = ModelProfiles.Resolve("all-minilm-l6-v2", ModelProfiles.Builtin);
        // 512 tokens * 4 bytes = 2048 < configured 4000 → profile wins.
        Assert.Equal(2048, ModelProfiles.ContextBudgetBytes(4000, profile));
        // Unknown/zero-window profile keeps the configured cap.
        Assert.Equal(4000, ModelProfiles.ContextBudgetBytes(4000, null));
    }
}
