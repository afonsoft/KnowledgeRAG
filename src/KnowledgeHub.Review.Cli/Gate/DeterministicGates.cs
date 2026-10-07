using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.Gate;

/// <summary>Gate verdict: <c>proceed</c> | <c>skip</c> (leave untouched) |
/// <c>block</c> (do not approve/merge) | <c>inconclusive</c> (pending).</summary>
public sealed record GateResult(string Outcome, IReadOnlyList<string> Reasons)
{
    public bool Proceeds => Outcome == "proceed";
    public static GateResult Proceed() => new("proceed", []);
}

/// <summary>
/// RF-003 — deterministic vetoes evaluated before any LLM decision:
/// draft, skip label, unresolved human CHANGES_REQUESTED, failing/pending
/// required checks, missing LLM secret (comment-only), non-mergeable state,
/// external fork author, blocked stack layers.
/// </summary>
public static class DeterministicGates
{
    public static GateResult Evaluate(PullRequestSignal signal, ReviewOptions options)
    {
        var reasons = new List<string>();
        var m = signal.Meta;

        if (m.IsDraft) reasons.Add("draft PR");
        if (m.Labels.Any(l => string.Equals(l, options.SkipLabel, StringComparison.OrdinalIgnoreCase)))
            reasons.Add($"label '{options.SkipLabel}'");
        if (reasons.Count > 0) return new GateResult("skip", reasons);

        var humanBlock = signal.HumanReviews
            .Where(r => r.State == "CHANGES_REQUESTED")
            .Select(r => $"human CHANGES_REQUESTED by {r.Author}");
        reasons.AddRange(humanBlock);

        var failing = signal.Checks
            .Where(c => c.IsRequired && c.Conclusion is "failure" or "cancelled" or "timed_out" or "action_required")
            .Select(c => $"required check '{c.Name}' {c.Conclusion}");
        reasons.AddRange(failing);

        var pending = signal.Checks
            .Where(c => c.IsRequired && c.Status is not "completed" and not "success")
            .Select(c => $"required check '{c.Name}' still {c.Status}");
        var pendingList = pending.ToList();
        if (pendingList.Count > 0)
            return new GateResult("inconclusive", reasons.Concat(pendingList).ToList());

        if (m.MergeableState is "dirty" or "behind" or "blocked" or "unstable")
            reasons.Add($"mergeStateStatus={m.MergeableState}");
        if (m.FromFork)
            reasons.Add("PR from fork — comment-only mode, never auto-merge");
        if (signal.IsStacked && signal.StackChain.Any(l => l.ChecksFailing))
            reasons.Add("stack layer below has failing checks (stackBlocked)");

        return reasons.Count > 0 ? new GateResult("block", reasons) : GateResult.Proceed();
    }
}
