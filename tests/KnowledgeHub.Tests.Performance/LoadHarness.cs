using System.Collections.Concurrent;
using System.Diagnostics;

namespace KnowledgeHub.Tests.Performance;

/// <summary>Result of a sustained-load run.</summary>
public sealed record LoadReport(
    string Name,
    int TotalRequests,
    int Errors,
    double ElapsedSeconds,
    double RequestsPerSecond,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    public double ErrorRate => (double)Errors / Math.Max(1, TotalRequests);

    public override string ToString() =>
        $"{Name}: {TotalRequests} req in {ElapsedSeconds:F1}s " +
        $"({RequestsPerSecond:F1} rps, {Errors} errors) | " +
        $"p50 {P50Ms:F0}ms, p95 {P95Ms:F0}ms, p99 {P99Ms:F0}ms, max {MaxMs:F0}ms";
}

/// <summary>
/// Minimal sustained-load driver: <paramref name="workerCount"/> workers each
/// loop their operation until <paramref name="duration"/> elapses. Operation
/// setup (e.g. MCP session bootstrap, login) happens via
/// <paramref name="operationFactory"/> BEFORE the clock starts, so only
/// steady-state requests are measured. An operation that throws counts as an
/// error; successful ops record their latency.
/// </summary>
public static class LoadHarness
{
    public static async Task<LoadReport> RunAsync(
        string name,
        int workerCount,
        TimeSpan duration,
        Func<int, Task<Func<CancellationToken, Task>>> operationFactory,
        CancellationToken ct = default)
    {
        var operations = new List<Func<CancellationToken, Task>>(workerCount);
        for (var i = 0; i < workerCount; i++)
            operations.Add(await operationFactory(i));

        var latencies = new ConcurrentBag<double>();
        var errors = 0;
        var deadline = DateTimeOffset.UtcNow + duration;
        var started = Stopwatch.GetTimestamp();

        var workers = operations.Select(op => Task.Run(async () =>
        {
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    await op(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }

                latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
        }, ct));

        await Task.WhenAll(workers);
        var elapsed = Stopwatch.GetElapsedTime(started);

        var sorted = latencies.OrderBy(x => x).ToArray();
        double Percentile(double p) =>
            sorted.Length == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

        return new LoadReport(
            name,
            sorted.Length,
            errors,
            elapsed.TotalSeconds,
            sorted.Length / elapsed.TotalSeconds,
            Percentile(0.50),
            Percentile(0.95),
            Percentile(0.99),
            sorted.Length == 0 ? 0 : sorted[^1]);
    }
}
