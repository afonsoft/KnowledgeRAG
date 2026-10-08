using System.CommandLine;
using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Knowledge;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Review;

/// <summary>Shared CLI options passed as a single bundle — keeps command
/// handler signatures within the parameter-count budget (S107).</summary>
internal sealed record CliOptionsBundle(
    Option<string?> Input,
    Option<string?> Repo,
    Option<int?> Pr,
    Option<bool> Wait,
    Option<bool> DryRun,
    Option<string> Reasoning);

/// <summary>
/// Factories shared by the CLI subcommands — keeps Program.cs a thin
/// command table (complexity budget) and makes the wiring unit-inspectable.
/// </summary>
internal static class CliRuntime
{
    public static IGitHubApi? GitHub(ReviewOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GitHubToken))
        {
            Console.Error.WriteLine("GITHUB_TOKEN is required");
            return null;
        }
        return new OctokitGitHubApi(options.GitHubToken);
    }

    public static (string Owner, string Repo, int Pr)? Target(ParseResult r, Option<string?> repoOption,
        Option<int?> prOption, ReviewOptions options)
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

    public static ReviewPipeline Pipeline(ParseResult r, Option<bool> dryRunOption, Option<string> reasoningOption,
        ReviewOptions options)
    {
        var effective = options with
        {
            DryRun = options.DryRun || r.GetValue(dryRunOption),
            Reasoning = r.GetValue(reasoningOption) ?? options.Reasoning,
        };
        return new ReviewPipeline(
            GitHub(options)!,
            effective,
            _ => Task.FromResult(Chat(effective)),
            ct => HubKnowledgeBridge.ConnectAsync(effective, ct));
    }

    public static async Task<PullRequestSignal?> LoadSignalAsync(ParseResult r, CliOptionsBundle bundle,
        ReviewOptions options, CancellationToken ct)
    {
        if (r.GetValue(bundle.Input) is { } path && File.Exists(path))
            return System.Text.Json.JsonSerializer.Deserialize<PullRequestSignal>(
                await File.ReadAllTextAsync(path, ct), SignalJson.Options);
        var t = Target(r, bundle.Repo, bundle.Pr, options);
        if (t is null || GitHub(options) is null) return null;
        return await Pipeline(r, bundle.DryRun, bundle.Reasoning, options)
            .CollectAsync(t.Value.Owner, t.Value.Repo, t.Value.Pr, r.GetValue(bundle.Wait), ct);
    }

    private static IChatClient? Chat(ReviewOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.LlmEndpoint) || string.IsNullOrWhiteSpace(o.LlmModel))
            return null;
        return new OpenAiCompatChatClient(new HttpClient(), o.LlmEndpoint, o.LlmModel, o.LlmApiKey);
    }
}
