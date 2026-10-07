using KnowledgeHub.Review.Review;

namespace KnowledgeHub.Review.Gate;

/// <summary>Final review outcome driving publication.</summary>
public enum Verdict { Skipped, Blocked, Inconclusive, Approved, ChangesFound }

/// <summary>Combines the deterministic gate with the analysis result into a
/// publishable verdict (RF-003 + RF-006 rules).</summary>
public sealed record VerdictResult(
    Verdict Verdict,
    IReadOnlyList<string> Reasons,
    bool MayApprove,
    bool MayAutoMerge,
    string StatusState,   // success|failure|pending
    string StatusDescription);

public static class VerdictEngine
{
    public static VerdictResult Decide(GateResult gate, AnalysisResult analysis, bool llmAvailable)
    {
        if (gate.Outcome == "skip")
            return new(Verdict.Skipped, gate.Reasons, false, false, "success",
                $"Skipped: {string.Join("; ", gate.Reasons)}");
        if (gate.Outcome == "inconclusive")
            return new(Verdict.Inconclusive, gate.Reasons, false, false, "pending",
                "Required checks still pending");
        if (gate.Outcome == "block")
            return new(Verdict.Blocked, gate.Reasons, false, false, "failure",
                $"Blocked: {string.Join("; ", gate.Reasons)}");

        // Gate passed — decide by findings.
        var blocking = analysis.Findings.Where(f => f.BlocksMerge).ToList();
        if (blocking.Count > 0)
            return new(Verdict.ChangesFound, [$"{blocking.Count} severe/critical finding(s)"],
                false, false, "failure", "Severe or critical findings");

        if (analysis.Findings.Count > 0)
            return new(Verdict.ChangesFound, [$"{analysis.Findings.Count} finding(s) — none blocking"],
                true, false, "success", "Findings posted, no blockers");

        if (!llmAvailable)
            return new(Verdict.ChangesFound, ["LLM unavailable — deterministic-only review"],
                false, false, "pending", "LLM unavailable");

        return new(Verdict.Approved, [], true, true, "success", "Approved by Knowledge Review");
    }
}
