using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.GitHub;

/// <summary>
/// RF-002 — a PR is stacked when its <c>base.ref</c> is the head branch of
/// another open PR in the same repo. Walks the chain bottom-up and flags
/// <c>stackBlocked</c> when a lower layer has failing checks.
/// </summary>
public static class StackedPrDetector
{
    /// <summary>Returns the stack chain bottom→top (the current PR first whose
    /// base is another open PR's head, then that PR's base chain), empty when
    /// the PR targets trunk directly.</summary>
    public static IReadOnlyList<StackLayer> Detect(
        PrMeta current,
        IReadOnlyList<(int Number, string HeadRef, string BaseRef)> openPrs,
        IReadOnlyList<CheckSignal> checksForCurrent,
        Func<int, IReadOnlyList<CheckSignal>>? checksByPr = null)
    {
        var layers = new List<StackLayer>();
        var cursor = current.BaseRef;
        var visited = new HashSet<int>();

        while (true)
        {
            var below = openPrs.FirstOrDefault(p =>
                p.HeadRef == cursor && !visited.Contains(p.Number));
            if (below == default)
                break;
            if (!visited.Add(below.Number))
                break; // cycle guard

            var belowChecks = checksByPr?.Invoke(below.Number) ?? [];
            var failing = belowChecks.Any(c =>
                c.Conclusion is "failure" or "cancelled" or "timed_out" or "action_required");

            layers.Add(new StackLayer(below.Number, below.HeadRef, below.BaseRef, failing));
            cursor = below.BaseRef;
        }

        return layers;
    }

    /// <summary>Any lower stack layer with failing checks blocks merge.</summary>
    public static bool StackBlocked(IReadOnlyList<StackLayer> chain) =>
        chain.Any(l => l.ChecksFailing);
}
