using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Gate;
using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Knowledge;
using KnowledgeHub.Review.Output;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Review;

/// <summary>
/// Orchestrates collect → review → gate → publish. Every sub-command maps to
/// a stage; `run` chains them (RF-001..RF-009).
/// </summary>
public sealed class ReviewPipeline(
    IGitHubApi github,
    ReviewOptions options,
    Func<CancellationToken, Task<IChatClient?>> chatFactory,
    Func<CancellationToken, Task<HubKnowledgeBridge>> hubFactory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record RunOutput(
        PullRequestSignal Signal,
        AnalysisResult Analysis,
        GateResult Gate,
        VerdictResult Verdict,
        bool Published,
        string? Error);

    public async Task<PullRequestSignal> CollectAsync(string owner, string repo, int pr, bool wait, CancellationToken ct)
    {
        var collector = new GitHubSignalsCollector(github, options);
        if (!wait)
            return await collector.CollectAsync(owner, repo, pr, ct);
        var waiter = new SignalWaiter(options);
        return await waiter.WaitAsync(owner, repo, pr, c => collector.CollectAsync(owner, repo, pr, c), ct);
    }

    public async Task<RunOutput> ReviewAsync(PullRequestSignal signal, CancellationToken ct)
    {
        var gate = DeterministicGates.Evaluate(signal, options);
        var hub = await hubFactory(ct);
        var chat = await chatFactory(ct);

        AnalysisResult analysis;
        if (!gate.Proceeds || chat is null)
        {
            analysis = new AnalysisResult("comment", "", []);
        }
        else if (options.Reasoning == "hub" && hub.Reachable)
        {
            var instructions = InstructionLoader.Load(Directory.GetCurrentDirectory());
            var firstChunk = DiffChunker.Split(signal.Files, options.MaxDiffKb).FirstOrDefault();
            var prompt = ReviewPromptBuilder.BuildUserPrompt(
                signal, firstChunk is null ? "(empty diff)" : DiffChunker.Render(firstChunk),
                instructions, [], options, 0, 1);
            var answer = await hub.AgentChatAsync(prompt, ct);
            analysis = FindingClassifier.Parse(answer ?? "", options.MinConfidence);
        }
        else
        {
            var instructions = InstructionLoader.Load(Directory.GetCurrentDirectory());
            var knowledge = await hub.SearchConventionsAsync(signal.Repo, ct);
            analysis = await new DiffAnalyzer(chat, options).AnalyzeAsync(signal, instructions, knowledge, ct);
        }

        var verdict = VerdictEngine.Decide(gate, analysis, chat is not null);
        return new RunOutput(signal, analysis, gate, verdict, false, null);
    }

    public async Task<RunOutput> PublishAsync(RunOutput run, CancellationToken ct)
    {
        var signal = run.Signal;
        var analysis = run.Analysis;
        var verdict = run.Verdict;
        var (owner, repo) = SplitRepo(signal.Repo);

        if (verdict.Verdict is Verdict.Skipped)
            return run with { Published = false };

        if (options.DryRun)
        {
            var plan = new
            {
                status = new { state = verdict.StatusState, context = options.StatusContext, description = verdict.StatusDescription },
                summary = SummaryCommentRenderer.Render(signal, analysis, verdict, options.Language, true),
                review = new
                {
                    @event = verdict.MayApprove ? "APPROVE" : "COMMENT",
                    comments = InlineCommentMapper.Map(analysis.Findings, signal),
                },
                autoMerge = verdict.MayAutoMerge ? options.MergeMethod : null,
            };
            Console.WriteLine("DRY-RUN — planned mutations:");
            Console.WriteLine(JsonSerializer.Serialize(plan, Json));
            return run with { Published = false };
        }

        var hub = await hubFactory(ct);
        var summary = SummaryCommentRenderer.Render(signal, analysis, verdict, options.Language, hub.Reachable);

        // 1. commit status — always published so checks reflect the verdict.
        await github.CreateStatusAsync(owner, repo, signal.Meta.HeadSha,
            verdict.StatusState, options.StatusContext, verdict.StatusDescription,
            signal.Meta.HtmlUrl, ct);

        // 2. idempotent summary comment.
        await github.CreateOrUpdateSummaryCommentAsync(owner, repo, signal.Number,
            SummaryCommentRenderer.Marker, summary, ct);

        // 3. PR review with inline comments (never REQUEST_CHANGES — spec RF-005).
        var inline = InlineCommentMapper.Map(analysis.Findings, signal);
        var reviewEvent = verdict.MayApprove ? "APPROVE" : "COMMENT";
        await github.SubmitReviewAsync(owner, repo, signal.Number, signal.Meta.HeadSha,
            reviewEvent, null, inline, ct);

        // 4. auto-merge — only when verdict approved AND repo allows it.
        if (verdict.MayAutoMerge && await github.GetAllowAutoMergeAsync(owner, repo, ct))
            await github.EnableAutoMergeAsync(signal.Meta.NodeId, options.MergeMethod, ct);

        // 5. knowledge persistence — degrade silently if hub unreachable.
        if (options.WriteKnowledge && hub.Reachable)
            await hub.PersistAsync(signal, analysis, verdict.Verdict.ToString(), ct);

        return run with { Published = true };
    }

    public async Task<int> RunAsync(string owner, string repo, int pr, bool wait,
        PullRequestSignal? presetSignal, CancellationToken ct)
    {
        var signal = presetSignal ?? await CollectAsync(owner, repo, pr, wait, ct);
        var reviewed = await ReviewAsync(signal, ct);
        var published = await PublishAsync(reviewed, ct);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            verdict = published.Verdict.Verdict.ToString(),
            gate = published.Gate.Outcome,
            reasons = published.Verdict.Reasons,
            findings = published.Analysis.Findings.Count,
            published = published.Published,
            partial = published.Signal.Partial,
            stacked = published.Signal.IsStacked,
        }, Json));
        return published.Verdict.Verdict is Verdict.Blocked ? 2 : 0;
    }

    internal static (string Owner, string Repo) SplitRepo(string full)
    {
        var parts = full.Split('/', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : throw new ArgumentException($"repo must be owner/name, got '{full}'");
    }
}
