using KnowledgeHub.Review.GitHub;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class StackedPrDetectorTests
{
    [Fact]
    public void Direct_to_trunk_is_not_stacked()
    {
        var open = new List<(int, string, string)> { (2, "feature/other", "main") };
        var chain = StackedPrDetector.Detect(TestSignals.Meta(baseRef: "main"), open, []);
        Assert.Empty(chain);
        Assert.False(StackedPrDetector.StackBlocked(chain));
    }

    [Fact]
    public void Base_on_open_pr_head_is_stacked()
    {
        var open = new List<(int, string, string)> { (5, "feature/base", "main") };
        var chain = StackedPrDetector.Detect(TestSignals.Meta(baseRef: "feature/base"), open, []);
        var layer = Assert.Single(chain);
        Assert.Equal(5, layer.Number);
        Assert.Equal("feature/base", layer.HeadRef);
    }

    [Fact]
    public void Walks_multi_layer_chain()
    {
        var open = new List<(int, string, string)>
        {
            (5, "feature/base", "feature/lower"),
            (4, "feature/lower", "main"),
        };
        var chain = StackedPrDetector.Detect(TestSignals.Meta(baseRef: "feature/base"), open, []);
        Assert.Equal(2, chain.Count);
        Assert.Equal(5, chain[0].Number);
        Assert.Equal(4, chain[1].Number);
    }

    [Fact]
    public void Cycle_does_not_loop_forever()
    {
        var open = new List<(int, string, string)>
        {
            (5, "a", "b"),
            (6, "b", "a"),
        };
        var chain = StackedPrDetector.Detect(TestSignals.Meta(baseRef: "a"), open, []);
        Assert.Equal(2, chain.Count);
    }

    [Fact]
    public void Failing_checks_mark_layer_blocked()
    {
        var open = new List<(int, string, string)> { (5, "feature/base", "main") };
        var failing = new[] { TestSignals.Check("CI", "completed", "failure") };
        var chain = StackedPrDetector.Detect(
            TestSignals.Meta(baseRef: "feature/base"), open, [],
            checksByPr: n => n == 5 ? failing : []);
        Assert.True(chain[0].ChecksFailing);
        Assert.True(StackedPrDetector.StackBlocked(chain));
    }

    [Fact]
    public void Green_layer_is_not_blocked()
    {
        var open = new List<(int, string, string)> { (5, "feature/base", "main") };
        var chain = StackedPrDetector.Detect(
            TestSignals.Meta(baseRef: "feature/base"), open, [],
            checksByPr: _ => [TestSignals.Check("CI")]);
        Assert.False(StackedPrDetector.StackBlocked(chain));
    }
}
