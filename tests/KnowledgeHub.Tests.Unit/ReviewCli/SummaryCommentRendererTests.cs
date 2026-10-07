using KnowledgeHub.Review.Gate;
using KnowledgeHub.Review.Output;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class SummaryCommentRendererTests
{
    private static VerdictResult Approved() =>
        new(Verdict.Approved, [], true, true, "success", "Approved by Knowledge Review");

    [Fact]
    public void Carries_idempotent_marker()
    {
        var text = SummaryCommentRenderer.Render(TestSignals.Signal(),
            new AnalysisResult("approved", "clean", []), Approved(), "en", true);
        Assert.StartsWith(SummaryCommentRenderer.Marker, text);
        Assert.Equal("<!-- knowledge-review -->", SummaryCommentRenderer.Marker);
    }

    [Fact]
    public void Groups_findings_by_kind_sections()
    {
        var findings = new[]
        {
            new Finding("bug", "severe", "a.cs", 3, null, "npe", null),
            new Finding("style", "info", "b.cs", 5, null, "naming", null),
            new Finding("security", "warning", "c.cs", 7, "CWE-798", "hardcoded key", null),
            new Finding("flag", "investigate", null, null, null, "odd pattern", null),
        };
        var text = SummaryCommentRenderer.Render(TestSignals.Signal(),
            new AnalysisResult("request_changes", "", findings),
            new VerdictResult(Verdict.ChangesFound, ["1 blocking"], false, false, "failure", "Severe findings"),
            "en", true);
        Assert.Contains("### Bugs", text);
        Assert.Contains("### Style", text);
        Assert.Contains("### Security", text);
        Assert.Contains("### Flags", text);
        Assert.Contains("CWE-798", text);
        Assert.Contains("_repo-level_", text);
    }

    [Fact]
    public void Renders_portuguese_when_pt_br()
    {
        var text = SummaryCommentRenderer.Render(TestSignals.Signal(),
            new AnalysisResult("approved", "limpo", []), Approved(), "pt-BR", true);
        Assert.Contains("### Visão geral", text);
        Assert.Contains("### Veredito", text);
    }

    [Fact]
    public void Stacked_prs_render_chain()
    {
        var signal = TestSignals.Signal(
            meta: TestSignals.Meta(baseRef: "feature/base"),
            stacked: true,
            stack: [new StackLayer(5, "feature/base", "main", false)]);
        var text = SummaryCommentRenderer.Render(signal,
            new AnalysisResult("approved", "", []), Approved(), "en", true);
        Assert.Contains("### Stack", text);
        Assert.Contains("#5", text);
        Assert.Contains("layer", text);
    }

    [Fact]
    public void Hub_unreachable_adds_local_mode_note()
    {
        var text = SummaryCommentRenderer.Render(TestSignals.Signal(),
            new AnalysisResult("approved", "", []), Approved(), "en", hubReachable: false);
        Assert.Contains("local mode", text);
    }

    [Fact]
    public void Summary_only_mode_is_disclosed()
    {
        var text = SummaryCommentRenderer.Render(TestSignals.Signal(),
            new AnalysisResult("comment", "", [], SummaryOnly: true), Approved(), "en", true);
        Assert.Contains("summary mode", text);
    }
}
