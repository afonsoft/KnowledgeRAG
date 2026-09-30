using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Search;

/// <summary>SPEC-20260924-corrective-rag RF-001: retrieval evidence quality.</summary>
public enum RetrievalGrade { Insufficient = 0, Weak = 1, Sufficient = 2 }

public sealed record RetrievalGrading(RetrievalGrade Grade, double Confidence);

/// <summary>
/// Grades a retrieved result set before generation (CRAG-lite). Implementations
/// must never throw — a grading failure returns
/// <see cref="RetrievalGrade.Sufficient"/> with confidence 0 (fail-open,
/// preserves the pre-grading pipeline).
/// </summary>
public interface IRetrievalGrader
{
    Task<RetrievalGrading> GradeAsync(
        string query, IReadOnlyList<SearchResultItem> results, CancellationToken ct = default);
}

/// <summary>
/// Zero-cost grader: empty results are insufficient; otherwise thresholds on the
/// fused RRF score (when a breakdown exists) or the raw similarity score.
/// Scales differ — RRF fuses ranks (~0.016 per single-list top1), cosine spans
/// 0..1 — so separate configurable thresholds apply per breakdown presence.
/// </summary>
public sealed class HeuristicRetrievalGrader(IConfiguration configuration) : IRetrievalGrader
{
    public Task<RetrievalGrading> GradeAsync(
        string query, IReadOnlyList<SearchResultItem> results, CancellationToken ct = default)
    {
        if (results.Count == 0)
            return Task.FromResult(new RetrievalGrading(RetrievalGrade.Insufficient, 1.0));

        var top = results[0];
        var fused = top.ScoreBreakdown?.Fused;
        var score = fused ?? top.Score;
        var (sufficient, insufficient) = fused is not null
            ? (configuration.GetValue("Search:Grading:FusedSufficientScore", 0.020),
               configuration.GetValue("Search:Grading:FusedInsufficientScore", 0.010))
            : (configuration.GetValue("Search:Grading:SufficientScore", 0.55),
               configuration.GetValue("Search:Grading:InsufficientScore", 0.30));

        // Confidence scales with margin above/below the thresholds.
        if (score >= sufficient)
            return Task.FromResult(new RetrievalGrading(
                RetrievalGrade.Sufficient, Math.Min(1.0, score / sufficient - 1 + 0.5)));
        if (score < insufficient)
            return Task.FromResult(new RetrievalGrading(
                RetrievalGrade.Insufficient, Math.Min(1.0, 1 - score / insufficient + 0.5)));
        return Task.FromResult(new RetrievalGrading(RetrievalGrade.Weak, 0.5));
    }
}

/// <summary>
/// LLM grader (opt-in <c>Search:Grading:Mode=llm</c>): one batched prompt judges
/// the top candidates as relevant/not-relevant to the question.
/// ≥60% relevant → Sufficient, none → Insufficient, otherwise Weak.
/// </summary>
public sealed class LlmRetrievalGrader(
    IServiceProvider services,
    ILogger<LlmRetrievalGrader> logger) : IRetrievalGrader
{
    private const int MaxJudged = 5;

    public async Task<RetrievalGrading> GradeAsync(
        string query, IReadOnlyList<SearchResultItem> results, CancellationToken ct = default)
    {
        if (results.Count == 0)
            return new RetrievalGrading(RetrievalGrade.Insufficient, 1.0);

        var chat = services.GetService<Assistant.IAssistantChatClientProvider>()
            ?.ForSubtask("grade", services.GetService<IChatClient>())
            ?? services.GetService<IChatClient>();
        if (chat is null)
            return new RetrievalGrading(RetrievalGrade.Sufficient, 0);

        var judged = results.Take(MaxJudged).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append("For each numbered passage, decide whether it helps answer the question.\n")
          .Append("Reply with a JSON array of true/false, one per passage, nothing else.\n\n");
        for (var i = 0; i < judged.Count; i++)
        {
            var text = judged[i].ChunkText;
            sb.Append('[').Append(i + 1).Append("] ")
              .Append(text.Length > 400 ? text[..400] : text)
              .Append("\n\n");
        }
        sb.Append("Question: ").Append(query);

        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, sb.ToString())],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 64 },
                ct);
            var verdicts = ParseVerdicts(response.Text);
            if (verdicts is null || verdicts.Count == 0)
            {
                logger.LogWarning("Retrieval grading returned unparseable output — fail-open");
                return new RetrievalGrading(RetrievalGrade.Sufficient, 0);
            }
            var relevant = verdicts.Count(v => v);
            var ratio = (double)relevant / verdicts.Count;
            var grade = ratio >= 0.6 ? RetrievalGrade.Sufficient
                : relevant == 0 ? RetrievalGrade.Insufficient
                : RetrievalGrade.Weak;
            return new RetrievalGrading(grade, Math.Abs(ratio - 0.5) * 2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "LLM retrieval grading failed — fail-open");
            return new RetrievalGrading(RetrievalGrade.Sufficient, 0);
        }
    }

    private static List<bool>? ParseVerdicts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<bool>>(text[start..(end + 1)]);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
