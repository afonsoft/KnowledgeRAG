using KnowledgeHub.Server.Eval;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// SPEC-20260924-eval-regression-gate: gate evaluation over run metrics.
/// </summary>
public sealed class EvalGateTests
{
    private static readonly EvalMetricsSummary Metrics = new()
    {
        RecallAtK = 0.82,
        PrecisionAtK = 0.4,
        Mrr = 0.7,
        HitRate = 0.9,
        NdcgAtK = 0.75
    };
    private static readonly EvalLatencySummary Latency = new()
    {
        P50 = 120,
        P95 = 900,
        P99 = 1400,
        Mean = 300
    };

    [Fact]
    public void Gate_AllRulesPass_Pass()
    {
        var rules = new List<EvalGateRule>
        {
            new() { Metric = "recall_at_k", Direction = "gte", Threshold = 0.8 },
            new() { Metric = "p95_ms", Direction = "lte", Threshold = 1500 }
        };

        var result = EvalRunner.EvaluateGate(rules, Metrics, Latency, 2000, null);

        Assert.Equal("pass", result.Status);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Gate_RecallBelowThreshold_FailsWithViolation()
    {
        var rules = new List<EvalGateRule>
        {
            new() { Metric = "recall_at_k", Direction = "gte", Threshold = 0.9 }
        };

        var result = EvalRunner.EvaluateGate(rules, Metrics, Latency, 2000, "golden-v1");

        Assert.Equal("fail", result.Status);
        Assert.Single(result.Violations);
        Assert.Contains("recall_at_k", result.Violations[0]);
        Assert.Equal("golden-v1", result.BaselineName);
    }

    [Fact]
    public void Gate_UnknownMetric_RecordedAsViolation()
    {
        var rules = new List<EvalGateRule>
        {
            new() { Metric = "nonexistent", Direction = "gte", Threshold = 0 }
        };

        var result = EvalRunner.EvaluateGate(rules, Metrics, Latency, 100, null);

        Assert.Equal("fail", result.Status);
        Assert.Contains("unavailable", result.Violations[0]);
    }

    [Fact]
    public void Gate_FaithfulnessNull_ViolationNotSilentPass()
    {
        var rules = new List<EvalGateRule>
        {
            new() { Metric = "faithfulness", Direction = "gte", Threshold = 0.5 }
        };

        var result = EvalRunner.EvaluateGate(rules, Metrics, Latency, 100, null);

        Assert.Equal("fail", result.Status); // null metric can't satisfy a gate
    }

    [Fact]
    public void Gate_DurationMsRule_UsesRunDuration()
    {
        var rules = new List<EvalGateRule>
        {
            new() { Metric = "duration_ms", Direction = "lte", Threshold = 5000 }
        };

        Assert.Equal("pass", EvalRunner.EvaluateGate(rules, Metrics, null, 3000, null).Status);
        Assert.Equal("fail", EvalRunner.EvaluateGate(rules, Metrics, null, 9000, null).Status);
    }

    [Fact]
    public void Gate_RetrievalMetrics_HitRateAndNdcg()
    {
        // R6 (audit 2026-10-03): the gate must be able to regress on retrieval
        // quality, not only synthesis — hit_rate + ndcg resolve from the summary.
        var pass = new List<EvalGateRule>
        {
            new() { Metric = "hit_rate", Direction = "gte", Threshold = 0.8 },
            new() { Metric = "ndcg_at_k", Direction = "gte", Threshold = 0.7 }
        };
        Assert.Equal("pass", EvalRunner.EvaluateGate(pass, Metrics, Latency, 100, null).Status);

        var fail = new List<EvalGateRule>
        {
            new() { Metric = "hit_rate", Direction = "gte", Threshold = 0.95 },
            new() { Metric = "ndcg", Direction = "gte", Threshold = 0.8 }
        };
        var result = EvalRunner.EvaluateGate(fail, Metrics, Latency, 100, null);
        Assert.Equal("fail", result.Status);
        Assert.Equal(2, result.Violations.Count);
    }
}
