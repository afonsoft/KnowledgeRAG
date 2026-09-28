using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>POST /api/ask — RAG with server-side answer synthesis (SPEC-20260914-llm-answer-synthesis RF-003).</summary>
public static class AskEndpoints
{
    public static RouteGroupBuilder MapAskApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ask");

        group.MapPost("/", async (
            AskRequest request,
            CorrectiveRetrievalService retrieval,
            IAnswerService answers,
            Audit.Evidence.IEvidenceChainService? evidence,
            ILoggerFactory? loggerFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return Results.BadRequest(new { error = "question is required" });

            var mode = SearchEndpoints.ParseMode(request.Mode);
            if (mode is null)
                return Results.BadRequest(new { error = "mode must be hybrid | semantic | lexical" });

            if (!Search.ResolvedSearchFilter.TryResolve(request.Filters, out var filter, out var filterError))
                return Results.BadRequest(new { error = filterError });

            var generate = request.Generate ?? answers.IsConfigured;
            var k = request.TopK is null or <= 0 ? SearchEndpoints.DefaultTopK : Math.Min(request.TopK.Value, SearchEndpoints.MaxTopK);
            var outcome = await retrieval.RetrieveAsync(request.Question, k, request.SourceId, mode.Value, filter, ct: ct);
            var context = outcome.Results;
            var grade = retrieval.GradingEnabled
                ? outcome.Grading.Grade.ToString().ToLowerInvariant()
                : (string?)null;

            if (!generate)
            {
                return Results.Ok(new AskResponse
                {
                    Answer = null,
                    Citations = [],
                    LatencyMs = 0,
                    Model = null,
                    Generated = false,
                    Context = context,
                    RetrievalGrade = grade,
                    Retried = outcome.Retried
                });
            }

            // SPEC-20260924-corrective-rag RF-003: abstain without synthesis.
            if (outcome.Grading.Grade == Search.RetrievalGrade.Insufficient)
                return Results.Ok(retrieval.BuildAbstention(request.Question, outcome) with { Context = context });

            if (!answers.IsConfigured)
                return Results.BadRequest(new { error = "chat provider not configured (Chat:Provider=none)" });

            var answer = (await answers.AnswerAsync(request.Question, context, ct)) with
            {
                RetrievalGrade = grade,
                Retried = outcome.Retried
            };
            // SPEC-20260927-cryptographic-evidence-provenance-chain RF-002.
            var apiKeyId = http.User.FindFirst(Auth.ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
            await Audit.Evidence.EvidenceEmission.RecordAskAsync(
                evidence, loggerFactory?.CreateLogger("EvidenceEmission"),
                $"rest:{apiKeyId ?? "session"}", apiKeyId,
                request.Question, context, answer.Answer ?? "", ct);
            return Results.Ok(answer);
        });

        return group;
    }
}
