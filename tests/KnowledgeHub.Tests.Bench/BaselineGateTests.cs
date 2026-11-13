using System.Text.Json;
using KnowledgeHub.Tests.Bench.Journeys;
using Xunit.Abstractions;

namespace KnowledgeHub.Tests.Bench;

/// <summary>
/// SPEC-20261113-agent-journey-bench RF-003: runs every named journey and
/// gates it against tests/eval/journeys/baseline.json — calls ≤ maxCalls
/// (hard, structural friction) and totalMs ≤ budgetMs (hard, generous);
/// warnMs only logs. A journey without a baseline entry fails. Setting
/// BENCH_UPDATE_BASELINE=1 regenerates the baseline file (local only).
/// </summary>
public class BaselineGateTests : IClassFixture<JourneyRunner.Factory>
{
    private readonly JourneyRunner.Factory _factory;
    private readonly ITestOutputHelper _out;

    public BaselineGateTests(JourneyRunner.Factory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public async Task All_Journeys_Meet_Baseline()
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var journeys = MemoryJourneys.All(runId);
        var baselines = LoadBaselines(out var baselinePath);
        var update = Environment.GetEnvironmentVariable("BENCH_UPDATE_BASELINE") == "1";
        var observed = new Dictionary<string, JourneyBaseline>();
        var runner = new JourneyRunner(_factory);
        await runner.ConnectAsync();

        foreach (var journey in journeys)
        {
            var result = await runner.RunAsync(journey);
            Report(result);

            if (update)
            {
                observed[journey.Name] = new JourneyBaseline(
                    result.Calls, (int)Math.Max(result.TotalMs * 10, 10_000),
                    (int)Math.Max(result.TotalMs * 5, 5_000));
                continue;
            }

            Assert.True(baselines.TryGetValue(journey.Name, out var baseline),
                $"journey '{journey.Name}' has no baseline entry in {baselinePath} — " +
                "run with BENCH_UPDATE_BASELINE=1 to add it");
            Gate(result, baseline!);
        }

        if (update)
            File.WriteAllText(baselinePath,
                JsonSerializer.Serialize(observed, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Gate_Fails_On_Extra_Calls()
    {
        // AC: a journey needing +1 call vs baseline must fail — test the test.
        var result = new JourneyResult { Name = "synthetic" };
        for (var i = 0; i < 5; i++)
            result.Steps.Add(new StepMetric("memory_list", 1, 10, 10, false));
        var ex = Record.Exception(
            () => Gate(result, new JourneyBaseline(4, 60_000, 30_000)));
        Assert.NotNull(ex);
        Assert.Contains("synthetic", ex!.Message);
        Assert.Contains("calls", ex.Message);
    }

    [Fact]
    public void Gate_Fails_On_Missing_Baseline() =>
        Assert.False(new Dictionary<string, JourneyBaseline>()
            .ContainsKey("never-baselined"));

    private void Gate(JourneyResult result, JourneyBaseline baseline)
    {
        Assert.True(result.Calls <= baseline.MaxCalls,
            $"journey '{result.Name}' calls {result.Calls} > maxCalls {baseline.MaxCalls} — friction regression");
        Assert.True(result.TotalMs <= baseline.BudgetMs,
            $"journey '{result.Name}' totalMs {result.TotalMs} > budgetMs {baseline.BudgetMs}");
        if (result.TotalMs > baseline.WarnMs)
            _out.WriteLine($"WARN journey '{result.Name}' totalMs {result.TotalMs} > warnMs {baseline.WarnMs}");
    }

    private void Report(JourneyResult result)
    {
        _out.WriteLine($"journey {result.Name}: calls={result.Calls} totalMs={result.TotalMs} " +
            $"bytesIn={result.BytesIn} bytesOut={result.BytesOut}");
        foreach (var s in result.Steps)
            _out.WriteLine($"  {s.Tool,-24} {s.Ms,5}ms in={s.BytesIn} out={s.BytesOut} err={s.IsError}");
    }

    private static Dictionary<string, JourneyBaseline> LoadBaselines(out string path)
    {
        path = BaselinePath();
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject().ToDictionary(
                p => p.Name,
                p => new JourneyBaseline(
                    p.Value.GetProperty("maxCalls").GetInt32(),
                    p.Value.GetProperty("budgetMs").GetInt32(),
                    p.Value.GetProperty("warnMs").GetInt32()));
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException($"malformed baseline file: {path}", ex);
        }
    }

    private static string BaselinePath()
    {
        // Walk up from the test assembly dir to the repo root (slnx marker).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "KnowledgeHub.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Join(dir!.FullName, "tests", "eval", "journeys", "baseline.json");
    }
}
