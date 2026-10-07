using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Signals;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Review.Review;

/// <summary>
/// RF-004 — runs the chunked diff through <see cref="IChatClient"/> map-reduce:
/// each chunk analyzed <c>REVIEW_PASSES</c> times, consensus-merged by
/// <see cref="FindingClassifier"/>. Oversized diffs degrade to summary mode.
/// </summary>
public sealed class DiffAnalyzer(IChatClient chat, ReviewOptions options)
{
    public async Task<AnalysisResult> AnalyzeAsync(
        PullRequestSignal signal,
        InstructionLoader.Instructions instructions,
        IReadOnlyList<string> hubKnowledge,
        CancellationToken ct)
    {
        var oversized = DiffChunker.IsOversized(signal.Files, options.MaxDiffKb);
        var chunks = DiffChunker.Split(signal.Files, options.MaxDiffKb);
        if (chunks.Count == 0)
            return new AnalysisResult("approved", "Empty diff — nothing to review.", [], oversized);

        var all = new List<AnalysisResult>();
        foreach (var chunk in chunks)
        {
            var prompt = ReviewPromptBuilder.BuildUserPrompt(
                signal, DiffChunker.Render(chunk), instructions, hubKnowledge,
                options, chunk.Index, chunks.Count);

            var passResults = new List<AnalysisResult>();
            for (var i = 0; i < options.Passes; i++)
            {
                var response = await chat.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, ReviewPromptBuilder.SystemPrompt),
                     new ChatMessage(ChatRole.User, prompt)],
                    new ChatOptions { MaxOutputTokens = 4000, Temperature = i == 0 ? 0f : 0.3f },
                    ct);
                passResults.Add(FindingClassifier.Parse(response.Text ?? "", options.MinConfidence));
            }
            all.Add(FindingClassifier.Merge(passResults, options.Passes));
        }

        var merged = FindingClassifier.Merge(all, 1); // chunk merge: union, no extra threshold
        return merged with
        {
            SummaryOnly = oversized,
            Summary = oversized
                ? $"⚠️ Diff above {options.MaxDiffKb}KB — summary mode; only highest-risk files reviewed.\n\n{merged.Summary}"
                : merged.Summary,
        };
    }
}
