using System.CommandLine;
using System.Text.Json;
using KnowledgeHub.Review;
using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;

var options = ReviewOptions.FromEnvironment();

var prOption = new Option<int?>("--pr") { Description = "Pull request number (default: GITHUB_REF_NAME)" };
var repoOption = new Option<string?>("--repo") { Description = "owner/repo (default: GITHUB_REPOSITORY)" };
var waitOption = new Option<bool>("--wait-for-signals") { Description = "Poll required checks until REVIEW_SIGNAL_TIMEOUT_MIN" };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Print planned mutations, change nothing" };
var reasoningOption = new Option<string>("--reasoning") { Description = "local|hub — LLM backend", DefaultValueFactory = _ => "local" };
var inputOption = new Option<string?>("--input") { Description = "signal.json path (review/gate without re-collecting)" };

static void AddShared(Command c, params Option[] opts)
{
    foreach (var o in opts) c.Options.Add(o);
}

var root = new RootCommand("knowledge-review — self-hosted AI code review (SPEC-20261007)");

var collect = new Command("collect", "Collect PR signals → signal.json");
AddShared(collect, prOption, repoOption, waitOption, dryRunOption, reasoningOption);
collect.SetAction(async (r, ct) =>
{
    var gh = CliRuntime.GitHub(options); if (gh is null) return 1;
    var t = CliRuntime.Target(r, repoOption, prOption, options); if (t is null) return 1;
    var signal = await CliRuntime.Pipeline(r, dryRunOption, reasoningOption, options)
        .CollectAsync(t.Value.Owner, t.Value.Repo, t.Value.Pr, r.GetValue(waitOption), ct);
    Console.WriteLine(JsonSerializer.Serialize(signal, SignalJson.Options));
    return 0;
});
root.Subcommands.Add(collect);

var review = new Command("review", "Analyze diff with LLM → findings + verdict");
AddShared(review, prOption, repoOption, waitOption, dryRunOption, reasoningOption, inputOption);
review.SetAction(async (r, ct) =>
{
    var signal = await CliRuntime.LoadSignalAsync(r, inputOption, repoOption, prOption,
        waitOption, dryRunOption, reasoningOption, options, ct);
    if (signal is null) return 1;
    var run = await CliRuntime.Pipeline(r, dryRunOption, reasoningOption, options).ReviewAsync(signal, ct);
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
});
root.Subcommands.Add(review);

var gate = new Command("gate", "Evaluate deterministic gates only");
AddShared(gate, prOption, repoOption, waitOption, dryRunOption, reasoningOption, inputOption);
gate.SetAction(async (r, ct) =>
{
    var signal = await CliRuntime.LoadSignalAsync(r, inputOption, repoOption, prOption,
        waitOption, dryRunOption, reasoningOption, options, ct);
    if (signal is null) return 1;
    var result = KnowledgeHub.Review.Gate.DeterministicGates.Evaluate(signal, options);
    Console.WriteLine(JsonSerializer.Serialize(result, SignalJson.Options));
    return result.Outcome == "proceed" ? 0 : 2;
});
root.Subcommands.Add(gate);

var run = new Command("run", "Full pipeline: collect → review → gate → publish");
AddShared(run, prOption, repoOption, waitOption, dryRunOption, reasoningOption, inputOption);
run.SetAction(async (r, ct) =>
{
    var gh = CliRuntime.GitHub(options); if (gh is null) return 1;
    var t = CliRuntime.Target(r, repoOption, prOption, options); if (t is null) return 1;
    PullRequestSignal? preset = null;
    if (r.GetValue(inputOption) is { } inPath && File.Exists(inPath))
        preset = JsonSerializer.Deserialize<PullRequestSignal>(await File.ReadAllTextAsync(inPath, ct), SignalJson.Options);
    return await CliRuntime.Pipeline(r, dryRunOption, reasoningOption, options)
        .RunAsync(t.Value.Owner, t.Value.Repo, t.Value.Pr, r.GetValue(waitOption), preset, ct);
});
root.Subcommands.Add(run);

return await root.Parse(args).InvokeAsync();
