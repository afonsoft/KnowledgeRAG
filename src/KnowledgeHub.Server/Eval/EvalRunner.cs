using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Eval;

/// <summary>
/// SPEC-20260923-eval-harness RF-004: sequential, read-only eval runner —
/// executes the dataset against the live search (and optionally ask) pipeline,
/// aggregates metrics, persists the run, and diffs vs. a reference run.
/// Never mutates the index; per-case failures are recorded, not fatal.
/// </summary>
public sealed class EvalRunner(
    ISearchService search,
    IServiceProvider services,
    KnowledgeHubDbContext db,
    ILogger<EvalRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int DefaultTopK = 10;

    /// <summary>Run knobs for <see cref="RunAsync"/> — all optional; omitted
    /// fields fall back to eval defaults or the case's own values.</summary>
    public sealed record EvalRunOptions(
        string? Mode = null, int? TopK = null, string? Faithfulness = null,
        Guid? CompareTo = null, string? BaselineName = null,
        IReadOnlyList<EvalGateRule>? Gate = null);

    public async Task<EvalReport> RunAsync(
        IReadOnlyList<EvalCase> cases, string datasetJson,
        EvalRunOptions? options = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var defaultMode = ParseMode(options?.Mode) ?? SearchMode.Hybrid;
        var defaultK = options?.TopK is > 0 ? options.TopK.Value : DefaultTopK;
        var faith = options?.Faithfulness?.ToLowerInvariant() ?? "none";

        var datasetHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(datasetJson)));

        // SPEC-20260924-eval-regression-gate RF-001: named baseline resolves to
        // its run id — and pins the dataset fingerprint.
        var baselineName = options?.BaselineName;
        var compareTo = options?.CompareTo;
        if (baselineName is { } bn)
        {
            var baseline = await db.EvalBaselines.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Name == bn, ct)
                ?? throw new KeyNotFoundException($"baseline '{bn}' not found");
            // SPEC-20260926-search-correctness-and-stream RF-004: a baseline is
            // only comparable when it ran on the SAME dataset — the pinned
            // fingerprint must match the current one.
            if (!string.Equals(baseline.DatasetHash, datasetHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"baseline '{bn}' was pinned to a different dataset " +
                    $"(baseline {baseline.DatasetHash[..Math.Min(12, baseline.DatasetHash.Length)]}… " +
                    $"vs current {datasetHash[..12]}…) — re-pin the baseline or run without it");
            compareTo = baseline.EvalRunId;
        }

        var results = new List<EvalCaseResult>(cases.Count);
        foreach (var evalCase in cases)
        {
            ct.ThrowIfCancellationRequested();
            var caseSw = Stopwatch.StartNew();
            try
            {
                results.Add(await RunCaseAsync(evalCase, defaultMode, defaultK, faith, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "eval case {CaseId} failed", evalCase.Id);
                results.Add(new EvalCaseResult
                {
                    CaseId = evalCase.Id,
                    Hit = false,
                    Recall = 0,
                    Precision = 0,
                    ReciprocalRank = 0,
                    Error = ex.Message,
                    LatencyMs = caseSw.Elapsed.TotalMilliseconds,
                    Tags = evalCase.Tags
                });
            }
        }

        var faithValues = results.Where(r => r.Faithfulness is not null).Select(r => r.Faithfulness!.Value).ToList();
        var metrics = new EvalMetricsSummary
        {
            RecallAtK = EvalMetrics.Round(results.Average(r => r.Recall)),
            PrecisionAtK = EvalMetrics.Round(results.Average(r => r.Precision)),
            Mrr = EvalMetrics.Round(results.Average(r => r.ReciprocalRank)),
            Faithfulness = faithValues.Count > 0 ? EvalMetrics.Round(faithValues.Average()) : null,
            FaithfulnessSkippedReason = FaithSkipReason(faith, faithValues.Count)
        };

        // RF-002: latency percentiles over per-case wall time.
        var latency = LatencySummary(results);

        var report = new EvalReport
        {
            RunId = Guid.NewGuid(),
            StartedAt = startedAt,
            DurationMs = sw.ElapsedMilliseconds,
            DatasetHash = datasetHash,
            Cases = cases.Count,
            Metrics = metrics,
            Results = results,
            Latency = latency,
            BaselineName = baselineName
        };

        // RF-001: gate evaluation on the computed metrics.
        EvalGateResult? gateResult = null;
        if (options?.Gate is { Count: > 0 } gate)
            gateResult = EvaluateGate(gate, metrics, latency, report.DurationMs, baselineName);

        db.EvalRuns.Add(new EvalRun
        {
            Id = report.RunId,
            StartedAt = startedAt,
            DurationMs = report.DurationMs,
            DatasetHash = report.DatasetHash,
            MetricsJson = JsonSerializer.Serialize(metrics, Json),
            PerCaseJson = JsonSerializer.Serialize(results, Json),
            LatencyJson = latency is null ? null : JsonSerializer.Serialize(latency, Json),
            GateResultJson = gateResult is null ? null : JsonSerializer.Serialize(gateResult, Json),
            BaselineName = baselineName
        });
        await db.SaveChangesAsync(CancellationToken.None);

        if (compareTo is { } prevId)
        {
            var delta = await BuildDeltaAsync(prevId, report, ct);
            if (delta is null)
                throw new KeyNotFoundException($"compareTo run '{prevId}' not found");
            report = report with { Delta = delta };
        }
        return report with { Gate = gateResult };
    }

    /// <summary>RF-002: p50/p95/p99/mean over per-case latencies.</summary>
    private static EvalLatencySummary? LatencySummary(IReadOnlyList<EvalCaseResult> results)
    {
        var ms = results.Where(r => r.LatencyMs is not null)
            .Select(r => r.LatencyMs!.Value).OrderBy(v => v).ToList();
        if (ms.Count == 0)
            return null;
        return new EvalLatencySummary
        {
            P50 = EvalMetrics.Round(PercentileOf(ms, 50)),
            P95 = EvalMetrics.Round(PercentileOf(ms, 95)),
            P99 = EvalMetrics.Round(PercentileOf(ms, 99)),
            Mean = EvalMetrics.Round(ms.Average())
        };
    }

    private static double PercentileOf(IReadOnlyList<double> sorted, int p)
    {
        var rank = (p / 100.0) * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>RF-001: evaluate gate rules against the run's metrics — every
    /// violation is listed, never silently swallowed.</summary>
    internal static EvalGateResult EvaluateGate(
        IReadOnlyList<EvalGateRule> rules, EvalMetricsSummary metrics,
        EvalLatencySummary? latency, long durationMs, string? baselineName)
    {
        var violations = new List<string>();
        foreach (var rule in rules)
        {
            var value = MetricValue(rule.Metric, metrics, latency, durationMs);
            if (value is null)
            {
                violations.Add($"{rule.Metric}: metric unavailable (skipped — value is null)");
                continue;
            }
            var pass = rule.Direction.ToLowerInvariant() switch
            {
                "gte" => value >= rule.Threshold,
                "lte" => value <= rule.Threshold,
                "gt" => value > rule.Threshold,
                "lt" => value < rule.Threshold,
                _ => false
            };
            if (!pass)
                violations.Add(
                    $"{rule.Metric} {rule.Direction} {rule.Threshold}: actual {EvalMetrics.Round(value.Value)}");
        }
        return new EvalGateResult
        {
            Status = violations.Count == 0 ? "pass" : "fail",
            Violations = violations,
            BaselineName = baselineName
        };
    }

    internal static double? MetricValue(
        string metric, EvalMetricsSummary metrics, EvalLatencySummary? latency, long durationMs) =>
        metric.ToLowerInvariant() switch
        {
            "recall_at_k" or "recall" => metrics.RecallAtK,
            "precision_at_k" or "precision" => metrics.PrecisionAtK,
            "mrr" => metrics.Mrr,
            "faithfulness" => metrics.Faithfulness,
            "p50_ms" => latency?.P50,
            "p95_ms" => latency?.P95,
            "p99_ms" => latency?.P99,
            "mean_ms" => latency?.Mean,
            "duration_ms" => durationMs,
            _ => null
        };

    private async Task<EvalCaseResult> RunCaseAsync(
        EvalCase evalCase, SearchMode defaultMode, int defaultK, string faith, CancellationToken ct)
    {
        var mode = ParseMode(evalCase.Mode) ?? defaultMode;
        var k = evalCase.TopK is > 0 ? evalCase.TopK.Value : defaultK;
        // RF (SPEC-20260926-ops-and-ui-polish): latency percentiles measure the
        // RETRIEVAL call — the old case-wide stopwatch folded optional LLM
        // answer/judge time into p95, inflating it well beyond search latency.
        var searchSw = Stopwatch.StartNew();
        var results = await search.SearchAsync(evalCase.Question, k, null, mode, filter: null, ct: ct);
        var searchMs = searchSw.Elapsed.TotalMilliseconds;

        var recall = EvalMetrics.RecallAtK(results, evalCase);
        var precision = EvalMetrics.PrecisionAtK(results, evalCase);
        var rr = EvalMetrics.ReciprocalRank(results, evalCase);
        var hit = evalCase.ExpectNoAnswer ? results.Count == 0 : results.Any(r => EvalMetrics.IsHit(r, evalCase));

        double? faithfulness = null;
        if ((faith is "keyword" or "llm") && !evalCase.ExpectNoAnswer && evalCase.ExpectedTextMarkers.Count > 0)
        {
            var answers = services.GetService<IAnswerService>();
            var answer = answers is { IsConfigured: true }
                ? await answers.AnswerAsync(evalCase.Question, results, ct)
                : null;
            if (answer?.Answer is { } text)
            {
                faithfulness = faith == "keyword"
                    ? EvalMetrics.Round(
                        (double)evalCase.ExpectedTextMarkers.Count(m =>
                            text.Contains(m, StringComparison.OrdinalIgnoreCase))
                        / evalCase.ExpectedTextMarkers.Count)
                    : await JudgeAsync(text, evalCase, ct);
            }
        }

        return new EvalCaseResult
        {
            CaseId = evalCase.Id,
            Recall = EvalMetrics.Round(recall),
            Precision = EvalMetrics.Round(precision),
            ReciprocalRank = EvalMetrics.Round(rr),
            Hit = hit,
            Inconsistent = evalCase.ExpectNoAnswer && results.Count > 0,
            Faithfulness = faithfulness,
            LatencyMs = searchMs,
            Tags = evalCase.Tags
        };
    }

    /// <summary>RF-003 llm faithfulness: judge prompt, never receives case-foreign context.</summary>
    private async Task<double?> JudgeAsync(string answer, EvalCase evalCase, CancellationToken ct)
    {
        if (services.GetService<IChatClient>() is not { } chat)
            return null;
        try
        {
            var prompt = $"Answer: {answer}\n\nExpected key points: {string.Join("; ", evalCase.ExpectedTextMarkers)}\n\n" +
                "Respond with JSON only: {\"claims\": <n>, \"supportedClaims\": <n>} — " +
                "claims = factual statements in the answer, supportedClaims = those consistent with the expected key points.";
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var doc = JsonDocument.Parse(response.Text);
            var claims = doc.RootElement.GetProperty("claims").GetInt32();
            var supported = doc.RootElement.GetProperty("supportedClaims").GetInt32();
            return claims > 0 ? EvalMetrics.Round((double)supported / claims) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "llm faithfulness judge failed for case {CaseId}", evalCase.Id);
            return null;
        }
    }

    private string? FaithSkipReason(string faith, int scored)
    {
        if (faith is not ("keyword" or "llm"))
            return null;
        if (scored > 0)
            return null;
        return faith == "llm" && services.GetService<IChatClient>() is null
            ? "chat provider not configured"
            : "no cases with expectedTextMarkers";
    }

    private async Task<EvalDelta?> BuildDeltaAsync(Guid prevId, EvalReport current, CancellationToken ct)
    {
        var prev = await db.EvalRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == prevId, ct);
        if (prev is null)
            return null;
        var prevMetrics = JsonSerializer.Deserialize<EvalMetricsSummary>(prev.MetricsJson, Json)!;
        var prevCases = JsonSerializer.Deserialize<List<EvalCaseResult>>(prev.PerCaseJson, Json)!;
        var prevHits = prevCases.Where(c => c.Hit).Select(c => c.CaseId).ToHashSet();
        return new EvalDelta
        {
            CompareRunId = prevId,
            RecallAtKDelta = EvalMetrics.Round(current.Metrics.RecallAtK - prevMetrics.RecallAtK),
            PrecisionAtKDelta = EvalMetrics.Round(current.Metrics.PrecisionAtK - prevMetrics.PrecisionAtK),
            MrrDelta = EvalMetrics.Round(current.Metrics.Mrr - prevMetrics.Mrr),
            Regressions = current.Results
                .Where(r => !r.Hit && prevHits.Contains(r.CaseId))
                .Select(r => r.CaseId).ToList()
        };
    }

    public static SearchMode? ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "hybrid" => SearchMode.Hybrid,
        "semantic" => SearchMode.Semantic,
        "lexical" => SearchMode.Lexical,
        _ => null
    };
}
