using KnowledgeHub.Review.Review;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class FindingClassifierTests
{
    [Fact]
    public void Parses_clean_json()
    {
        var raw = """
            {"verdict":"request_changes","summary":"one bug",
             "findings":[{"kind":"bug","severity":"severe","file":"src/Foo.cs","line":12,
                          "cwe":"476","confidence":0.9,"rationale":"null deref","suggestion":"check"}]}
            """;
        var r = FindingClassifier.Parse(raw, 0.6);
        Assert.Equal("request_changes", r.Verdict);
        Assert.Equal("one bug", r.Summary);
        var f = Assert.Single(r.Findings);
        Assert.Equal("bug", f.Kind);
        Assert.Equal("severe", f.Severity);
        Assert.Equal("CWE-476", f.Cwe);       // normalized to CWE- prefix
        Assert.Equal(12, f.Line);
        Assert.True(f.BlocksMerge);
    }

    [Fact]
    public void Tolerates_prose_around_json()
    {
        var raw = "Here is my analysis:\n{\"verdict\":\"approved\",\"summary\":\"ok\",\"findings\":[]}\nDone.";
        var r = FindingClassifier.Parse(raw, 0.6);
        Assert.Equal("approved", r.Verdict);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Invalid_json_returns_comment_verdict()
    {
        var r = FindingClassifier.Parse("not json at all", 0.6);
        Assert.Equal("comment", r.Verdict);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Unknown_kind_and_severity_are_normalized()
    {
        var raw = """
            {"verdict":"comment","summary":"",
             "findings":[{"kind":"nitpick","severity":"fatal","file":"a.cs","line":1,"confidence":1.0,"rationale":"x"}]}
            """;
        var f = Assert.Single(FindingClassifier.Parse(raw, 0.6).Findings);
        Assert.Equal("flag", f.Kind);
        Assert.Equal("info", f.Severity);
    }

    [Fact]
    public void Low_confidence_findings_are_dropped()
    {
        var raw = """
            {"verdict":"comment","summary":"",
             "findings":[{"kind":"bug","severity":"severe","file":"a.cs","line":1,"confidence":0.4,"rationale":"x"},
                         {"kind":"bug","severity":"severe","file":"a.cs","line":2,"confidence":0.9,"rationale":"y"}]}
            """;
        var r = FindingClassifier.Parse(raw, 0.7);
        Assert.Single(r.Findings);
        Assert.Equal(2, r.Findings[0].Line);
    }

    [Fact]
    public void Merge_requires_consensus_across_passes()
    {
        var f1 = new Finding("bug", "severe", "a.cs", 10, null, "x", null, 0.9);
        var f2 = new Finding("style", "info", "b.cs", 2, null, "y", null, 0.8);
        // f1 in 2/3 passes → survives; f2 in 1/3 → dropped.
        var merged = FindingClassifier.Merge([
            new AnalysisResult("comment", "s1", [f1, f2]),
            new AnalysisResult("comment", "s2", [f1]),
            new AnalysisResult("comment", "s3", [f1]),
        ], passes: 3);
        var kept = Assert.Single(merged.Findings);
        Assert.Equal(10, kept.Line);
    }

    [Fact]
    public void Merge_keeps_highest_confidence_duplicate()
    {
        var low = new Finding("bug", "severe", "a.cs", 1, null, "x", null, 0.61);
        var high = low with { Confidence = 0.95 };
        var merged = FindingClassifier.Merge([
            new AnalysisResult("comment", "", [low]),
            new AnalysisResult("comment", "", [high]),
        ], passes: 2);
        Assert.Equal(0.95, Assert.Single(merged.Findings).Confidence);
    }

    [Fact]
    public void Merge_verdict_follows_blocking_findings()
    {
        var blocking = new Finding("security", "critical", "a.cs", 1, "CWE-79", "xss", null, 0.9);
        var merged = FindingClassifier.Merge([new AnalysisResult("comment", "", [blocking])], passes: 1);
        Assert.Equal("request_changes", merged.Verdict);

        var none = FindingClassifier.Merge([new AnalysisResult("approved", "", [])], passes: 1);
        Assert.Equal("approved", none.Verdict);
    }

    [Fact]
    public void Merge_verdict_is_comment_when_findings_do_not_block()
    {
        var nit = new Finding("style", "info", "a.cs", 1, null, "naming", null, 0.9);
        var merged = FindingClassifier.Merge([new AnalysisResult("comment", "", [nit])], passes: 1);
        Assert.Equal("comment", merged.Verdict);
        Assert.Single(merged.Findings);
    }

    [Fact]
    public void Merge_empty_returns_comment()
    {
        var merged = FindingClassifier.Merge([], passes: 2);
        Assert.Equal("comment", merged.Verdict);
        Assert.Empty(merged.Findings);
    }
}
