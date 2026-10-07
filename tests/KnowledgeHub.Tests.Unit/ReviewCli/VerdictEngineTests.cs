using KnowledgeHub.Review.Gate;
using KnowledgeHub.Review.Review;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class VerdictEngineTests
{
    private static AnalysisResult NoFindings() => new("approved", "", []);

    [Fact]
    public void Skip_gate_maps_to_skipped()
    {
        var v = VerdictEngine.Decide(new GateResult("skip", ["draft PR"]), NoFindings(), true);
        Assert.Equal(Verdict.Skipped, v.Verdict);
        Assert.Equal("success", v.StatusState);
        Assert.False(v.MayApprove);
    }

    [Fact]
    public void Inconclusive_gate_maps_to_pending()
    {
        var v = VerdictEngine.Decide(new GateResult("inconclusive", ["check pending"]), NoFindings(), true);
        Assert.Equal(Verdict.Inconclusive, v.Verdict);
        Assert.Equal("pending", v.StatusState);
        Assert.False(v.MayAutoMerge);
    }

    [Fact]
    public void Block_gate_never_approves()
    {
        var v = VerdictEngine.Decide(new GateResult("block", ["human CR"]), NoFindings(), true);
        Assert.Equal(Verdict.Blocked, v.Verdict);
        Assert.Equal("failure", v.StatusState);
        Assert.False(v.MayApprove);
        Assert.False(v.MayAutoMerge);
    }

    [Fact]
    public void Severe_bug_blocks_merge()
    {
        var analysis = new AnalysisResult("request_changes", "", [
            new Finding("bug", "severe", "src/Foo.cs", 10, null, "null deref", "check null")]);
        var v = VerdictEngine.Decide(GateResult.Proceed(), analysis, true);
        Assert.Equal(Verdict.ChangesFound, v.Verdict);
        Assert.Equal("failure", v.StatusState);
        Assert.False(v.MayApprove);
        Assert.False(v.MayAutoMerge);
    }

    [Fact]
    public void Critical_security_blocks_merge()
    {
        var analysis = new AnalysisResult("request_changes", "", [
            new Finding("security", "critical", "src/A.cs", 3, "CWE-89", "sql injection", "parameterize")]);
        var v = VerdictEngine.Decide(GateResult.Proceed(), analysis, true);
        Assert.Equal(Verdict.ChangesFound, v.Verdict);
        Assert.False(v.MayAutoMerge);
    }

    [Fact]
    public void Non_blocking_findings_approve_without_automerge()
    {
        var analysis = new AnalysisResult("comment", "", [
            new Finding("style", "info", "src/Foo.cs", 2, null, "naming", "rename")]);
        var v = VerdictEngine.Decide(GateResult.Proceed(), analysis, true);
        Assert.Equal(Verdict.ChangesFound, v.Verdict);
        Assert.True(v.MayApprove);
        Assert.False(v.MayAutoMerge); // findings exist → human should look
    }

    [Fact]
    public void Clean_analysis_approves_and_automerges()
    {
        var v = VerdictEngine.Decide(GateResult.Proceed(), NoFindings(), llmAvailable: true);
        Assert.Equal(Verdict.Approved, v.Verdict);
        Assert.Equal("success", v.StatusState);
        Assert.True(v.MayApprove);
        Assert.True(v.MayAutoMerge);
    }

    [Fact]
    public void Missing_llm_degrades_to_comment_only()
    {
        var v = VerdictEngine.Decide(GateResult.Proceed(), NoFindings(), llmAvailable: false);
        Assert.Equal(Verdict.ChangesFound, v.Verdict);
        Assert.Equal("pending", v.StatusState);
        Assert.False(v.MayApprove);
    }
}
