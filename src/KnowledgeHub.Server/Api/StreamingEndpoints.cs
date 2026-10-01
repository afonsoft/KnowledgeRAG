using System.Runtime.CompilerServices;
using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SSE streaming endpoints (SPEC-20260914-streaming-answers RF-001):
/// POST /api/ask/stream and POST /api/agent/stream emit token/tool_start/
/// tool_end/awaiting_approval/done/error events; heartbeat every 15 s;
/// X-Accel-Buffering disabled for nginx; client disconnect cancels the work.
/// </summary>
public static class StreamingEndpoints
{
    public static void MapStreamingApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ask/stream", AskStreamAsync)
            .RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting("llm");

        app.MapPost("/api/agent/stream", AgentStreamAsync)
            .RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting("llm");
    }

    private static async Task AskStreamAsync(
        HttpContext http, AskRequest request,
        CorrectiveRetrievalService retrieval, IAnswerService answers)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "question is required" }, http.RequestAborted);
            return;
        }
        var mode = SearchEndpoints.ParseMode(request.Mode);
        if (mode is null)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "mode must be hybrid | semantic | lexical" }, http.RequestAborted);
            return;
        }
        var generate = request.Generate ?? answers.IsConfigured;
        if (!generate || !answers.IsConfigured)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "chat provider not configured (Chat:Provider=none)" }, http.RequestAborted);
            return;
        }

        if (!Search.ResolvedSearchFilter.TryResolve(request.Filters, out var streamFilter, out var streamFilterError))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = streamFilterError }, http.RequestAborted);
            return;
        }

        var ct = http.RequestAborted;
        var k = request.TopK is null or <= 0 ? SearchEndpoints.DefaultTopK : Math.Min(request.TopK.Value, SearchEndpoints.MaxTopK);

        // SPEC-20260926-search-correctness-and-stream RF-002: the stream
        // gets the same retrieve→grade→retry/abstain pipeline as POST
        // /api/ask — previously it bypassed grading entirely.
        var outcome = await retrieval.RetrieveAsync(
            request.Question, k, request.SourceId, mode.Value, streamFilter, ct: ct);
        await WriteSseAsync(http, StreamAskOutcomeAsync(retrieval, answers, request.Question, outcome, ct), ct);
    }

    private static async IAsyncEnumerable<SseEvent> StreamAskOutcomeAsync(
        CorrectiveRetrievalService retrieval, IAnswerService answers,
        string question, CorrectiveRetrievalService.RetrievalOutcome outcome,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new SseEvent("meta", new
        {
            effectiveQuery = outcome.EffectiveQuery,
            corrected = !string.Equals(outcome.EffectiveQuery, question, StringComparison.Ordinal),
            retried = outcome.Retried,
            // RF-706: retry COUNT, not just a flag — clients and eval
            // need to tell 1 from 2 corrective attempts.
            retries = outcome.Retries,
            grade = retrieval.GradingEnabled
                ? outcome.Grading.Grade.ToString().ToLowerInvariant()
                : (string?)null
        });

        if (outcome.Grading.Grade == Search.RetrievalGrade.Insufficient)
        {
            var abstain = retrieval.BuildAbstention(question, outcome);
            yield return new SseEvent("abstain", abstain);
            // RF-705: the Playground only renders the terminal `done`
            // event — abstaining without it produced a blank answer.
            yield return new SseEvent("done", abstain);
            yield break;
        }

        await foreach (var e in answers
            .StreamAsync(question, outcome.Results, cancellationToken)
            .WithCancellation(cancellationToken))
            yield return e;
    }

    private static async Task AgentStreamAsync(HttpContext http, AgentRequest request, IAgentService agent)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Messages is not { Count: > 0 })
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "prompt or messages[] is required" }, http.RequestAborted);
            return;
        }
        if (!agent.IsConfigured)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "agent requires a chat provider (Chat:Provider)" }, http.RequestAborted);
            return;
        }

        var ct = http.RequestAborted;
        await WriteSseAsync(http, agent.StreamAsync(request, ct), ct);
    }

    /// <summary>Writes the event stream; exceptions become a terminal "error" event.</summary>
    private static async Task WriteSseAsync(
        HttpContext http, IAsyncEnumerable<SseEvent> events, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        var seq = 0;
        var gate = new SemaphoreSlim(1, 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    await gate.WaitAsync(stop.Token);
                    try { await http.Response.WriteAsync(": keep-alive\n\n", stop.Token); await http.Response.Body.FlushAsync(stop.Token); }
                    finally { gate.Release(); }
                }
            }
            catch (OperationCanceledException) { /* heartbeat stopped */ }
        });

        try
        {
            await foreach (var e in events.WithCancellation(ct))
            {
                var payload = JsonSerializer.Serialize(new { seq = ++seq, data = e.Data }, JsonSerializerOptions.Web);
                await gate.WaitAsync(ct);
                try
                {
                    await http.Response.WriteAsync($"event: {e.Type}\ndata: {payload}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected — stop quietly */ }
        catch (Exception ex)
        {
            var payload = JsonSerializer.Serialize(new { seq = seq + 1, data = new { message = ex.Message } }, JsonSerializerOptions.Web);
            try { await http.Response.WriteAsync($"event: error\ndata: {payload}\n\n", CancellationToken.None); await http.Response.Body.FlushAsync(); }
            catch { /* connection already gone */ }
        }
        finally
        {
            await stop.CancelAsync();
            try { await heartbeat; } catch { /* heartbeat already faulted/stopped */ } // NOSONAR — nada a observar: o heartbeat só existe p/ manter a conexão viva
        }
    }
}
