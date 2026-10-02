using System.Text.Json;
using KnowledgeHub.Server.Api;

namespace KnowledgeHub.Server.Eval;

/// <summary>
/// SPEC-20260924-eval-regression-gate RF-003: periodically runs the configured
/// dataset against the live pipeline, persists the run (with optional named
/// baseline + gate), and alerts on gate failure via warning log + optional
/// webhook POST (<c>Eval:Schedule:NotifyUrl</c>). Never mutates the index —
/// <see cref="EvalRunner"/> is read-only; a failing run never kills the host.
/// </summary>
public sealed class EvalScheduleService(
    IServiceProvider services,
    IConfiguration configuration,
    IWebHostEnvironment env,
    IHttpClientFactory httpClientFactory,
    ILogger<EvalScheduleService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Eval:Schedule:Enabled", false))
            return;

        var intervalMinutes = ResolveIntervalMinutes();
        var dataset = configuration.GetValue<string>("Eval:Schedule:Dataset");
        if (string.IsNullOrWhiteSpace(dataset))
        {
            logger.LogWarning("Eval:Schedule enabled but Eval:Schedule:Dataset is empty — scheduler idle");
            return;
        }

        logger.LogInformation("Eval schedule active: dataset '{Dataset}' every {Minutes}min",
            dataset, intervalMinutes);

        // First run shortly after startup so config mistakes surface early.
        await RunOnceSafeAsync(dataset, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceSafeAsync(dataset, stoppingToken);
    }

    private async Task RunOnceSafeAsync(string dataset, CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(dataset, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Scheduled eval run failed — continuing");
        }
    }

    private async Task RunOnceAsync(string dataset, CancellationToken ct)
    {
        var (datasetJson, error) = EvalEndpoints.ResolveDataset(dataset, env.ContentRootPath);
        if (datasetJson is null)
        {
            logger.LogWarning("Scheduled eval: {Error}", error);
            return;
        }
        var (cases, errors) = EvalDataset.Parse(datasetJson);
        if (errors.Count > 0)
        {
            logger.LogWarning("Scheduled eval: malformed dataset — {Errors}", string.Join("; ", errors));
            return;
        }

        var gate = ParseGate();
        var baseline = configuration.GetValue<string>("Eval:Schedule:Baseline");

        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<EvalRunner>();
        var report = await runner.RunAsync(cases, datasetJson,
            new EvalRunner.EvalRunOptions(
                Mode: configuration.GetValue<string>("Eval:Schedule:Mode"),
                TopK: configuration.GetValue<int?>("Eval:Schedule:TopK"),
                Faithfulness: configuration.GetValue<string>("Eval:Schedule:Faithfulness"),
                BaselineName: baseline, Gate: gate), ct);

        if (report.Gate?.Status == "fail")
        {
            logger.LogWarning(
                "EVAL GATE FAILED — violations: {Violations} (run {RunId})",
                string.Join("; ", report.Gate.Violations), report.RunId);
            await NotifyAsync(report, ct);
        }
        else
        {
            logger.LogInformation(
                "Scheduled eval completed: recall@k={Recall} mrr={Mrr} gate={Gate} (run {RunId})",
                report.Metrics.RecallAtK, report.Metrics.Mrr,
                report.Gate?.Status ?? "n/a", report.RunId);
        }
    }

    private int ResolveIntervalMinutes()
    {
        var cron = configuration.GetValue<string>("Eval:Schedule:Cron");
        return cron?.ToLowerInvariant() switch
        {
            "@daily" => 1440,
            "@weekly" => 10080,
            "@hourly" => 60,
            _ => Math.Clamp(configuration.GetValue("Eval:Schedule:IntervalMinutes", 1440), 15, 10080)
        };
    }

    private IReadOnlyList<EvalGateRule>? ParseGate()
    {
        var raw = configuration.GetValue<string>("Eval:Schedule:Gate");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<EvalGateRule>>(raw, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Eval:Schedule:Gate is not valid JSON — ignored");
            return null;
        }
    }

    private async Task NotifyAsync(EvalReport report, CancellationToken ct)
    {
        var url = configuration.GetValue<string>("Eval:Schedule:NotifyUrl");
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                report.RunId,
                report.StartedAt,
                report.DatasetHash,
                report.BaselineName,
                gate = report.Gate,
                report.Metrics,
                report.Latency
            }, Json);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            await httpClientFactory.CreateClient("eval-notify").PostAsync(url, content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Eval gate-failure webhook POST failed");
        }
    }
}
