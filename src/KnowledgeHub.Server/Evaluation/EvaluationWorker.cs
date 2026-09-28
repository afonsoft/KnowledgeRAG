using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Evaluation;

/// <summary>
/// Background worker (SPEC-20260927-rag-evaluation-triad-metrics RF-001/RF-003):
/// drains the evaluation channel, scores each answer with the triad evaluator,
/// persists the result and publishes telemetry. Runs off the request path.
/// </summary>
public sealed class EvaluationWorker(
    IRagEvaluationEnqueuer enqueuer,
    IRagTriadEvaluator evaluator,
    IDbContextFactory<KnowledgeHubDbContext> dbFactory,
    ILogger<EvaluationWorker> logger) : BackgroundService
{
    private const int QuestionCap = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Scope to a single reader; tolerate channel completion.
        var reader = enqueuer.Reader;
        try
        {
            await foreach (var task in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(task, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "RAG evaluation failed for query {QueryId}", task.QueryId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown — exit cleanly.
        }
    }

    internal async Task ProcessAsync(RagEvaluationTask task, CancellationToken ct)
    {
        var result = evaluator.Evaluate(task.Question, task.ContextChunks ?? [], task.Answer);
        RagEvaluationMetrics.Record(result);

        if (result.FlaggedAsHallucination)
        {
            logger.LogWarning(
                "RAG hallucination flagged for query {QueryId}: groundedness={G:F2}",
                task.QueryId, result.Groundedness);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.RagEvaluations.Add(new RagEvaluationEntity
        {
            QueryId = task.QueryId,
            Question = task.Question.Length > QuestionCap ? task.Question[..QuestionCap] : task.Question,
            ContextRelevance = result.ContextRelevance,
            Groundedness = result.Groundedness,
            AnswerRelevance = result.AnswerRelevance,
            OverallScore = result.OverallScore,
            TimestampUtc = task.QueuedAt,
            FlaggedAsHallucination = result.FlaggedAsHallucination
        });
        await db.SaveChangesAsync(ct);
    }
}
