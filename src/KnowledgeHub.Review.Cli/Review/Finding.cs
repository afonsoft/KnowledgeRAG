namespace KnowledgeHub.Review.Review;

/// <summary>
/// One review finding — Devin-Review-style taxonomy:
/// kind bug|style|security|flag; severity severe|non-severe|investigate|info|critical|warning.
/// </summary>
public sealed record Finding(
    string Kind,
    string Severity,
    string? File,
    int? Line,
    string? Cwe,
    string Rationale,
    string? Suggestion,
    double Confidence = 1.0)
{
    /// <summary>Severe/critical findings block approval and auto-merge.</summary>
    public bool BlocksMerge =>
        (Kind, Severity) is ("bug", "severe") or ("security", "critical");

    /// <summary>Identity for multi-pass dedupe — location + class, not text.</summary>
    public string DedupeKey =>
        $"{Kind}|{Severity}|{File ?? ""}|{Line ?? 0}|{(Cwe ?? "").ToUpperInvariant()}";
}

/// <summary>LLM analysis result for the whole diff.</summary>
public sealed record AnalysisResult(
    string Verdict,
    string Summary,
    IReadOnlyList<Finding> Findings,
    bool SummaryOnly = false);
