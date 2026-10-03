namespace KnowledgeHub.Shared.Contracts;

/// <summary>POST /api/eval/baselines — promote a run to a named baseline.</summary>
public sealed record BaselineRequest
{
    /// <summary>Baseline name (unique).</summary>
    public required string Name { get; init; }
    /// <summary>Run to promote.</summary>
    public required Guid RunId { get; init; }
}

/// <summary>GET /api/eval/runs list row.</summary>
public sealed record EvalRunSummaryDto
{
    public Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public long DurationMs { get; init; }
    public string DatasetHash { get; init; } = "";
    public EvalMetricsDto? Metrics { get; init; }
    public EvalGateDto? Gate { get; init; }
    public string? BaselineName { get; init; }
}

/// <summary>GET /api/eval/runs/{id} full report.</summary>
public sealed record EvalReportDto
{
    public Guid RunId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public long DurationMs { get; init; }
    public string DatasetHash { get; init; } = "";
    public int Cases { get; init; }
    public EvalMetricsDto? Metrics { get; init; }
    public List<EvalCaseDto> Results { get; init; } = [];
    public EvalDeltaDto? Delta { get; init; }
    public EvalLatencyDto? Latency { get; init; }
    public EvalGateDto? Gate { get; init; }
    public string? BaselineName { get; init; }
}

public sealed record EvalMetricsDto
{
    public double RecallAtK { get; init; }
    public double PrecisionAtK { get; init; }
    public double Mrr { get; init; }
    public double HitRate { get; init; }
    public double NdcgAtK { get; init; }
    public double? Faithfulness { get; init; }
    public string? FaithfulnessSkippedReason { get; init; }
}

public sealed record EvalLatencyDto
{
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Mean { get; init; }
}

public sealed record EvalCaseDto
{
    public string CaseId { get; init; } = "";
    public double Recall { get; init; }
    public double Precision { get; init; }
    public double ReciprocalRank { get; init; }
    public double Ndcg { get; init; }
    public bool Hit { get; init; }
    public bool Inconsistent { get; init; }
    public double? Faithfulness { get; init; }
    public string? Error { get; init; }
    public double? LatencyMs { get; init; }
}

public sealed record EvalDeltaDto
{
    public Guid CompareRunId { get; init; }
    public double RecallAtKDelta { get; init; }
    public double PrecisionAtKDelta { get; init; }
    public double MrrDelta { get; init; }
    public double HitRateDelta { get; init; }
    public double NdcgDelta { get; init; }
    public List<string> Regressions { get; init; } = [];
}

public sealed record EvalGateDto
{
    public string Status { get; init; } = "";
    public List<string> Violations { get; init; } = [];
    public string? BaselineName { get; init; }
}

/// <summary>GET /api/eval/baselines row.</summary>
public sealed record EvalBaselineDto
{
    public string Name { get; init; } = "";
    public Guid EvalRunId { get; init; }
    public string DatasetHash { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}
