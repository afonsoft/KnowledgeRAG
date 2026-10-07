using System.CommandLine;
using System.Text.Json;
using KnowledgeHub.Review;
using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Knowledge;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;
using Microsoft.Extensions.AI;

var options = ReviewOptions.FromEnvironment();

var prOption = new Option<int?>("--pr") { Description = "Pull request number (default: GITHUB_REF_NAME)" };
var repoOption = new Option<string?>("--repo") { Description = "owner/repo (default: GITHUB_REPOSITORY)" };
var waitOption = new Option<bool>("--wait-for-signals") { Description = "Poll required checks until REVIEW_SIGNAL_TIMEOUT_MIN" };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Print planned mutations, change nothing" };
var reasoningOption = new Option<string>("--reasoning") { Description = "local|hub — LLM backend", DefaultValueFactory = _ => "local" };
var inputOption = new Option<string?>("--input") { Description = "signal.json path (review/gate without re-collecting)" };

IGitHubApi? GitHub()
{
    if (string.IsNullOrWhiteSpace(options.GitHubToken))
    {
        Console.Error.WriteLine("GITHUB_TOKEN is required");
        return null;
    }
    return new OctokitGitHubApi(options.GitHubToken);
}

(string owner, string repo, int pr)? Target(ParseResult r)
{
    var full = r.GetValue(repoOption) ?? options.Repository;
    var pr = r.GetValue(prOption);
    if (string.IsNullOrWhiteSpace(full) || pr is null or <= 0)
    {
        Console.Error.WriteLine("missing --repo owner/repo and/or --pr N");
        return null;
    }
    var (o, rp) = ReviewPipeline.SplitRepo(full);
    return (o, rp, pr.Value);
}

ReviewPipeline Pipeline(ParseResult r)
{
    var effective = options with
    {
        DryRun = options.DryRun || r.GetValue(dryRunOption),
        Reasoning = r.GetValue(reasoningOption) ?? options.Reasoning,
    };
    return new ReviewPipeline(
        GitHub()!,
        effective,
        ct => Task.FromResult<IChatClient?>(Chat(effective)),
        ct => HubKnowledgeBridge.ConnectAsync(effective, ct));
}

static IChatClient? Chat(ReviewOptions o)
{
    if (string.IsNullOrWhiteSpace(o.LlmEndpoint) || string.IsNullOrWhiteSpace(o.LlmModel))
        return null;
    return new OpenAiCompatChatClient(new HttpClient(), o.LlmEndpoint, o.LlmModel, o.LlmApiKey);
}

var root = new RootCommand("knowledge-review — self-hosted AI code review (SPEC-20261007)");

var collect = new Command("collect", "Collect PR signals → signal.json");
collect.Options.Add(prOption); collect.Options.Add(repoOption);
collect.Options.Add(waitOption); collect.Options.Add(dryRunOption); collect.Options.Add(reasoningOption);
collect.SetAction(async (r, ct) =>
{
    var gh = GitHub(); if (gh is null) return 1;
    var t = Target(r); if (t is null) return 1;
    var signal = await Pipeline(r).CollectAsync(t.Value.owner, t.Value.repo, t.Value.pr, r.GetValue(waitOption), ct);
    Console.WriteLine(JsonSerializer.Serialize(signal, SignalJson.Options));
    return 0;
});
root.Subcommands.Add(collect);

var review = new Command("review", "Analyze diff with LLM → findings + verdict");
review.Options.Add(prOption); review.Options.Add(repoOption);
review.Options.Add(waitOption); review.Options.Add(dryRunOption); review.Options.Add(reasoningOption);
review.Options.Add(inputOption);
review.SetAction(async (r, ct) =>
{
    var signal = await LoadSignalAsync(r, ct); if (signal is null) return 1;
    var run = await Pipeline(r).ReviewAsync(signal, ct);
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
gate.Options.Add(prOption); gate.Options.Add(repoOption);
gate.Options.Add(waitOption); gate.Options.Add(dryRunOption); gate.Options.Add(reasoningOption);
gate.Options.Add(inputOption);
gate.SetAction(async (r, ct) =>
{
    var signal = await LoadSignalAsync(r, ct); if (signal is null) return 1;
    var result = KnowledgeHub.Review.Gate.DeterministicGates.Evaluate(signal, options);
    Console.WriteLine(JsonSerializer.Serialize(result, SignalJson.Options));
    return result.Outcome == "proceed" ? 0 : 2;
});
root.Subcommands.Add(gate);

var run = new Command("run", "Full pipeline: collect → review → gate → publish");
run.Options.Add(prOption); run.Options.Add(repoOption);
run.Options.Add(waitOption); run.Options.Add(dryRunOption); run.Options.Add(reasoningOption);
run.Options.Add(inputOption);
run.SetAction(async (r, ct) =>
{
    var gh = GitHub(); if (gh is null) return 1;
    var t = Target(r); if (t is null) return 1;
    PullRequestSignal? preset = null;
    if (r.GetValue(inputOption) is { } inPath && File.Exists(inPath))
        preset = JsonSerializer.Deserialize<PullRequestSignal>(await File.ReadAllTextAsync(inPath, ct), SignalJson.Options);
    return await Pipeline(r).RunAsync(t.Value.owner, t.Value.repo, t.Value.pr, r.GetValue(waitOption), preset, ct);
});
root.Subcommands.Add(run);

async Task<PullRequestSignal?> LoadSignalAsync(ParseResult r, CancellationToken ct)
{
    if (r.GetValue(inputOption) is { } path && File.Exists(path))
        return JsonSerializer.Deserialize<PullRequestSignal>(await File.ReadAllTextAsync(path, ct), SignalJson.Options);
    var t = Target(r); if (t is null || GitHub() is null) return null;
    return await Pipeline(r).CollectAsync(t.Value.owner, t.Value.repo, t.Value.pr, r.GetValue(waitOption), ct);
}

return await root.Parse(args).InvokeAsync();

internal static class SignalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
