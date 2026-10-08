using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeHub.Review.Review;

/// <summary>
/// Parses and normalizes the LLM JSON into <see cref="Finding"/>s: validates
/// kind/severity vocabulary, clamps confidence, applies REVIEW_MIN_CONFIDENCE
/// and dedupes multi-pass output by identity (file+line+kind+severity+CWE).
/// </summary>
public static class FindingClassifier
{
    private const string VerdictComment = "comment";

    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
        { "bug", "style", "security", "flag" };
    private static readonly HashSet<string> Severities = new(StringComparer.OrdinalIgnoreCase)
        { "severe", "non-severe", "investigate", "info", "critical", "warning" };

    /// <summary>Parse the raw LLM text into findings + verdict + summary.
    /// Tolerates prose around the JSON object.</summary>
    public static AnalysisResult Parse(string raw, double minConfidence)
    {
        var (verdict, summary, findings) = ExtractJson(raw);
        var filtered = findings
            .Where(f => f.Confidence >= minConfidence)
            .GroupBy(f => f.DedupeKey)
            .Select(g => g.OrderByDescending(f => f.Confidence).First())
            .ToList();
        return new AnalysisResult(verdict, summary, filtered);
    }

    /// <summary>Merge per-chunk results and apply multi-pass consensus:
    /// a finding survives when seen in ≥ ceil(passes/2) passes (same DedupeKey).</summary>
    public static AnalysisResult Merge(IReadOnlyList<AnalysisResult> results, int passes)
    {
        if (results.Count == 0)
            return new AnalysisResult(VerdictComment, "", []);

        var grouped = results.SelectMany(r => r.Findings)
            .GroupBy(f => f.DedupeKey);
        var threshold = Math.Max(1, (int)Math.Ceiling(passes / 2.0));
        var findings = grouped
            .Where(g => g.Count() >= Math.Min(threshold, results.Count))
            .Select(g => g.OrderByDescending(f => f.Confidence).First())
            .ToList();

        var summary = string.Join("\n\n", results.Select(r => r.Summary)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        var verdict = findings.Any(f => f.BlocksMerge) ? "request_changes"
            : findings.Count > 0 ? VerdictComment : "approved";
        return new AnalysisResult(verdict, summary, findings);
    }

    private static (string Verdict, string Summary, List<Finding>) ExtractJson(string raw)
    {
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
                return (VerdictComment, "", []);

            var node = JsonNode.Parse(raw[start..(end + 1)]);
            var verdict = node?["verdict"]?.GetValue<string>() ?? VerdictComment;
            var summary = node?["summary"]?.GetValue<string>() ?? "";
            var findings = node?["findings"] is JsonArray arr
                ? arr.OfType<JsonObject>().Select(ParseFinding).ToList()
                : [];
            return (verdict, summary, findings);
        }
        catch (JsonException)
        {
            return (VerdictComment, raw.Length > 800 ? raw[..800] : raw, []);
        }
    }

    private static Finding ParseFinding(JsonObject f)
    {
        var kind = f["kind"]?.GetValue<string>()?.ToLowerInvariant() ?? "flag";
        var severity = f["severity"]?.GetValue<string>()?.ToLowerInvariant() ?? "info";
        if (!Kinds.Contains(kind)) kind = "flag";
        if (!Severities.Contains(severity)) severity = "info";
        var cwe = f["cwe"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(cwe) && !cwe.StartsWith("CWE-", StringComparison.OrdinalIgnoreCase))
            cwe = $"CWE-{cwe}";
        return new Finding(
            kind, severity,
            f["file"]?.GetValue<string>(),
            f["line"] is JsonValue lv && lv.TryGetValue<int>(out var ln) ? ln : null,
            cwe,
            f["rationale"]?.GetValue<string>() ?? "",
            f["suggestion"]?.GetValue<string>(),
            f["confidence"] is JsonValue cv && cv.TryGetValue<double>(out var cd) ? cd : 1.0);
    }
}
