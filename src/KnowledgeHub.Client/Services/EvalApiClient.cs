using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Client for /api/eval/* (SPEC-20260925-eval-ui RF-001..RF-003).</summary>
public sealed class EvalApiClient(HttpClient http)
{
    /// <summary>GET /api/eval/runs — summaries (may include gate + baselineName).</summary>
    public Task<List<EvalRunSummaryDto>?> RunsAsync(int limit = 20, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<EvalRunSummaryDto>>($"api/eval/runs?limit={limit}", SharedJson.Options, ct);

    /// <summary>GET /api/eval/runs/{id} — full report.</summary>
    public Task<EvalReportDto?> RunAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<EvalReportDto>($"api/eval/runs/{id}", SharedJson.Options, ct);

    /// <summary>GET /api/eval/baselines.</summary>
    public Task<List<EvalBaselineDto>?> BaselinesAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<EvalBaselineDto>>("api/eval/baselines", SharedJson.Options, ct);

    /// <summary>POST /api/eval/run.</summary>
    public async Task<ApiResult<EvalReportDto>> RunEvalAsync(
        string dataset, string? mode, int? topK, string? faithfulness,
        string? baseline, string? gateJson, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["dataset"] = dataset };
        if (!string.IsNullOrWhiteSpace(mode)) payload["mode"] = mode;
        if (topK is not null) payload["topK"] = topK;
        if (!string.IsNullOrWhiteSpace(faithfulness) && faithfulness != "none")
            payload["faithfulness"] = faithfulness;
        if (!string.IsNullOrWhiteSpace(baseline)) payload["baseline"] = baseline;
        if (!string.IsNullOrWhiteSpace(gateJson))
            payload["gate"] = JsonSerializer.Deserialize<JsonElement>(gateJson);

        var response = await http.PostAsJsonAsync("api/eval/run", payload, SharedJson.Options, ct);
        return await ReadResultAsync<EvalReportDto>(response, ct);
    }

    /// <summary>POST /api/eval/baselines — promote a run to a named baseline.</summary>
    public async Task<ApiResult<object>> PromoteBaselineAsync(
        string name, Guid runId, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/eval/baselines", new { name, runId }, SharedJson.Options, ct);
        return await ReadResultAsync<object>(response, ct);
    }

    private static async Task<ApiResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return new ApiResult<T>((await response.Content.ReadFromJsonAsync<T>(SharedJson.Options, ct))!, null);
        var body = await response.Content.ReadAsStringAsync(ct);
        var error = ExtractError(body) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
        return new ApiResult<T>(default, error, body);
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }
}

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

public sealed record EvalBaselineDto
{
    public string Name { get; init; } = "";
    public Guid EvalRunId { get; init; }
    public string DatasetHash { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}
