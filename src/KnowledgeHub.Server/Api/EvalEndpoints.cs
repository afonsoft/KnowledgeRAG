using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Eval;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20260923-eval-harness RF-004: eval endpoints — POST /api/eval/run
/// executes a dataset (raw JSON array or a name under tests/eval/),
/// GET /api/eval/runs lists, GET /api/eval/runs/{id}?compare= diffs.
/// </summary>
public static class EvalEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record EvalRunRequest
    {
        /// <summary>Raw JSON array of cases, or a dataset name resolved under tests/eval/.</summary>
        public required string Dataset { get; init; }
        public int? TopK { get; init; }
        public string? Mode { get; init; }
        public string? Faithfulness { get; init; }
        public Guid? CompareTo { get; init; }
        /// <summary>SPEC-20260924-eval-regression-gate: named baseline — resolves
        /// to its run for compareTo + stamps the run.</summary>
        public string? Baseline { get; init; }
        /// <summary>Gate rules evaluated against the run's metrics.</summary>
        public List<EvalGateRule>? Gate { get; init; }
    }

    public sealed record BaselineRequest
    {
        /// <summary>Baseline name (unique).</summary>
        public required string Name { get; init; }
        /// <summary>Run to promote.</summary>
        public required Guid RunId { get; init; }
    }

    public static RouteGroupBuilder MapEvalApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/eval");

        MapRunEndpoints(group);
        MapRunDetailEndpoint(group);
        MapBaselineEndpoints(group);

        return group;
    }

    private static void MapRunEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/run", async (
            EvalRunRequest request, EvalRunner runner,
            IWebHostEnvironment env, CancellationToken ct) =>
        {
            var (datasetJson, resolveError) = ResolveDataset(request.Dataset, env.ContentRootPath);
            if (resolveError is not null)
                return Results.BadRequest(new { error = resolveError });

            var (cases, errors) = EvalDataset.Parse(datasetJson!);
            if (errors.Count > 0)
                return Results.BadRequest(new { error = "malformed dataset", errors });

            try
            {
                var report = await runner.RunAsync(
                    cases, datasetJson!, request.Mode, request.TopK,
                    request.Faithfulness, request.CompareTo,
                    request.Baseline, request.Gate, ct);
                return Results.Ok(report);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/runs", async (KnowledgeHubDbContext db, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 100);
            // SQLite cannot ORDER BY DateTimeOffset — sort in memory (runs are rare).
            var runs = (await db.EvalRuns.AsNoTracking()
                    .Select(r => new
                    {
                        r.Id,
                        r.StartedAt,
                        r.DurationMs,
                        r.DatasetHash,
                        r.MetricsJson,
                        r.GateResultJson,
                        r.BaselineName
                    })
                    .ToListAsync(ct))
                .OrderByDescending(r => r.StartedAt)
                .Take(take);
            return Results.Ok(runs.Select(r => new
            {
                r.Id,
                r.StartedAt,
                r.DurationMs,
                r.DatasetHash,
                metrics = JsonSerializer.Deserialize<EvalMetricsSummary>(r.MetricsJson, Json),
                gate = r.GateResultJson is null
                    ? null
                    : JsonSerializer.Deserialize<EvalGateResult>(r.GateResultJson, Json),
                r.BaselineName
            }));
        });
    }

    private static void MapRunDetailEndpoint(RouteGroupBuilder group)
    {
        group.MapGet("/runs/{id:guid}", async (
            Guid id, string? compare, KnowledgeHubDbContext db, CancellationToken ct) =>
        {
            var run = await db.EvalRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null)
                return Results.NotFound(new { error = "run not found" });

            var results = JsonSerializer.Deserialize<List<EvalCaseResult>>(run.PerCaseJson, Json) ?? [];
            var metrics = JsonSerializer.Deserialize<EvalMetricsSummary>(run.MetricsJson, Json)!;
            EvalDelta? delta = null;
            if (Guid.TryParse(compare, out var prevId))
            {
                var prev = await db.EvalRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == prevId, ct);
                if (prev is null)
                    return Results.NotFound(new { error = $"compare run '{prevId}' not found" });
                var prevMetrics = JsonSerializer.Deserialize<EvalMetricsSummary>(prev.MetricsJson, Json)!;
                var prevCases = JsonSerializer.Deserialize<List<EvalCaseResult>>(prev.PerCaseJson, Json) ?? [];
                var prevHits = prevCases.Where(c => c.Hit).Select(c => c.CaseId).ToHashSet();
                delta = new EvalDelta
                {
                    CompareRunId = prevId,
                    RecallAtKDelta = EvalMetrics.Round(metrics.RecallAtK - prevMetrics.RecallAtK),
                    PrecisionAtKDelta = EvalMetrics.Round(metrics.PrecisionAtK - prevMetrics.PrecisionAtK),
                    MrrDelta = EvalMetrics.Round(metrics.Mrr - prevMetrics.Mrr),
                    Regressions = results.Where(r => !r.Hit && prevHits.Contains(r.CaseId))
                        .Select(r => r.CaseId).ToList()
                };
            }

            return Results.Ok(new EvalReport
            {
                RunId = run.Id,
                StartedAt = run.StartedAt,
                DurationMs = run.DurationMs,
                DatasetHash = run.DatasetHash,
                Cases = results.Count,
                Metrics = metrics,
                Results = results,
                Delta = delta,
                Latency = run.LatencyJson is null
                    ? null
                    : JsonSerializer.Deserialize<EvalLatencySummary>(run.LatencyJson, Json),
                Gate = run.GateResultJson is null
                    ? null
                    : JsonSerializer.Deserialize<EvalGateResult>(run.GateResultJson, Json),
                BaselineName = run.BaselineName
            });
        });
    }

    private static void MapBaselineEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260924-eval-regression-gate RF-001: named baselines.
        group.MapGet("/baselines", async (KnowledgeHubDbContext db, CancellationToken ct) =>
        {
            var baselines = await db.EvalBaselines.AsNoTracking()
                .OrderBy(b => b.Name)
                .Select(b => new { b.Name, b.EvalRunId, b.DatasetHash, b.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(baselines);
        });

        /// <summary>Promote a run to a named baseline (upsert by name).</summary>
        group.MapPost("/baselines", async (
            BaselineRequest request, KnowledgeHubDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
                return Results.BadRequest(new { error = "name is required (max 100 chars)" });
            var run = await db.EvalRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RunId, ct);
            if (run is null)
                return Results.NotFound(new { error = $"run '{request.RunId}' not found" });

            var existing = await db.EvalBaselines
                .FirstOrDefaultAsync(b => b.Name == request.Name, ct);
            if (existing is null)
            {
                db.EvalBaselines.Add(new Domain.Entities.EvalBaseline
                {
                    Name = request.Name,
                    EvalRunId = request.RunId,
                    DatasetHash = run.DatasetHash
                });
            }
            else
            {
                existing.EvalRunId = request.RunId;
                existing.DatasetHash = run.DatasetHash;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.Name, request.RunId });
        });
    }

    /// <summary>
    /// Resolves the dataset: inline JSON array, or a name looked up under
    /// <c>tests/eval/</c> walking up from the content root (repo layout).
    /// </summary>
    internal static (string? Json, string? Error) ResolveDataset(string dataset, string contentRoot)
    {
        var trimmed = dataset.Trim();
        if (trimmed.StartsWith('['))
            return (trimmed, null);

        if (trimmed.IndexOfAny(new[] { '/', '\\', '.' }) >= 0 || trimmed.Length > 100)
            return (null, "dataset name must not contain path separators or dots");

        var dir = new DirectoryInfo(contentRoot);
        for (var i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "eval", $"{trimmed}.json");
            if (File.Exists(candidate))
                return (File.ReadAllText(candidate), null);
        }
        return (null, $"dataset '{trimmed}' not found under tests/eval/");
    }
}
