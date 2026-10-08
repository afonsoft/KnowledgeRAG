using System.CommandLine;
using System.Text.Json;
using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review;

/// <summary>
/// Command table + handlers for the CLI — extracted from Program.cs to keep
/// the top-level file within the cognitive-complexity budget (S3776).
/// </summary>
internal static class CliCommands
{
    public static RootCommand BuildRootCommand(ReviewOptions options)
    {
        var prOption = new Option<int?>("--pr") { Description = "Pull request number (default: GITHUB_REF_NAME)" };
        var repoOption = new Option<string?>("--repo") { Description = "owner/repo (default: GITHUB_REPOSITORY)" };
        var waitOption = new Option<bool>("--wait-for-signals") { Description = "Poll required checks until REVIEW_SIGNAL_TIMEOUT_MIN" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Print planned mutations, change nothing" };
        var reasoningOption = new Option<string>("--reasoning") { Description = "local|hub — LLM backend", DefaultValueFactory = _ => "local" };
        var inputOption = new Option<string?>("--input") { Description = "signal.json path (review/gate without re-collecting)" };
        var bundle = new CliOptionsBundle(inputOption, repoOption, prOption, waitOption, dryRunOption, reasoningOption);

        var root = new RootCommand("knowledge-review — self-hosted AI code review (SPEC-20261007)");

        var collect = new Command("collect", "Collect PR signals → signal.json");
        AddShared(collect, bundle);
        collect.SetAction(async (r, ct) => await CollectAsync(r, bundle, options, ct));
        root.Subcommands.Add(collect);

        var review = new Command("review", "Analyze diff with LLM → findings + verdict");
        AddShared(review, bundle, withInput: true);
        review.SetAction(async (r, ct) => await ReviewAsync(r, bundle, options, ct));
        root.Subcommands.Add(review);

        var gate = new Command("gate", "Evaluate deterministic gates only");
        AddShared(gate, bundle, withInput: true);
        gate.SetAction(async (r, ct) => await GateAsync(r, bundle, options, ct));
        root.Subcommands.Add(gate);

        var run = new Command("run", "Full pipeline: collect → review → gate → publish");
        AddShared(run, bundle, withInput: true);
        run.SetAction(async (r, ct) => await RunAsync(r, bundle, options, ct));
        root.Subcommands.Add(run);

        return root;
    }

    private static void AddShared(Command c, CliOptionsBundle bundle, bool withInput = false)
    {
        c.Options.Add(bundle.Pr);
        c.Options.Add(bundle.Repo);
        c.Options.Add(bundle.Wait);
        c.Options.Add(bundle.DryRun);
        c.Options.Add(bundle.Reasoning);
        if (withInput) c.Options.Add(bundle.Input);
    }

    private static async Task<int> CollectAsync(ParseResult r, CliOptionsBundle bundle, ReviewOptions options, CancellationToken ct)
    {
        var gh = CliRuntime.GitHub(options);
        if (gh is null) return 1;
        var t = CliRuntime.Target(r, bundle.Repo, bundle.Pr, options);
        if (t is null) return 1;
        var signal = await CliRuntime.Pipeline(r, bundle.DryRun, bundle.Reasoning, options)
            .CollectAsync(t.Value.Owner, t.Value.Repo, t.Value.Pr, r.GetValue(bundle.Wait), ct);
        Console.WriteLine(JsonSerializer.Serialize(signal, SignalJson.Options));
        return 0;
    }

    private static async Task<int> ReviewAsync(ParseResult r, CliOptionsBundle bundle, ReviewOptions options, CancellationToken ct)
    {
        var signal = await CliRuntime.LoadSignalAsync(r, bundle, options, ct);
        if (signal is null) return 1;
        var run = await CliRuntime.Pipeline(r, bundle.DryRun, bundle.Reasoning, options).ReviewAsync(signal, ct);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            verdict = run.Verdict.Verdict.ToString(),
            gate = run.Gate.Outcome,
            run.Verdict.Reasons,
            run.Analysis.Summary,
            run.Analysis.Findings,
            run.Analysis.SummaryOnly,
        }, SignalJson.Options));
        return 0;
    }

    private static async Task<int> GateAsync(ParseResult r, CliOptionsBundle bundle, ReviewOptions options, CancellationToken ct)
    {
        var signal = await CliRuntime.LoadSignalAsync(r, bundle, options, ct);
        if (signal is null) return 1;
        var result = KnowledgeHub.Review.Gate.DeterministicGates.Evaluate(signal, options);
        Console.WriteLine(JsonSerializer.Serialize(result, SignalJson.Options));
        return result.Outcome == "proceed" ? 0 : 2;
    }

    private static async Task<int> RunAsync(ParseResult r, CliOptionsBundle bundle, ReviewOptions options, CancellationToken ct)
    {
        var gh = CliRuntime.GitHub(options);
        if (gh is null) return 1;
        var t = CliRuntime.Target(r, bundle.Repo, bundle.Pr, options);
        if (t is null) return 1;
        PullRequestSignal? preset = null;
        if (r.GetValue(bundle.Input) is { } inPath && File.Exists(inPath))
            preset = JsonSerializer.Deserialize<PullRequestSignal>(await File.ReadAllTextAsync(inPath, ct), SignalJson.Options);
        return await CliRuntime.Pipeline(r, bundle.DryRun, bundle.Reasoning, options)
            .RunAsync(t.Value.Owner, t.Value.Repo, t.Value.Pr, r.GetValue(bundle.Wait), preset, ct);
    }
}
