using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Audit.Evidence;

/// <summary>
/// Best-effort emission helpers for the RAG/agent pipelines (RF-002). Every
/// call swallows store failures (logged at Warning) — evidence recording
/// must never break the answer path.
/// </summary>
public static class EvidenceEmission
{
    /// <summary>Common emission fields shared by the ask and tool chains.</summary>
    public sealed record EmissionContext(
        IEvidenceChainService? Evidence, string SessionId, string? ApiKeyId, ILogger? Logger);

    /// <summary>QuerySubmitted → ChunksRetrieved → AnswerSynthesized chain.</summary>
    public static async Task<EvidenceReceipt?> RecordAskAsync(
        EmissionContext ctx, string question,
        IReadOnlyList<SearchResultItem> chunks, string answer,
        CancellationToken ct)
    {
        if (ctx.Evidence is null)
            return null;
        // SPEC-20260928-observability-followups RF-002: span per emission chain
        // (QuerySubmitted → ChunksRetrieved → AnswerSynthesized).
        using var span = Telemetry.KnowledgeHubActivity.Start("evidence.emit");
        span?.SetTag("kind", "ask");
        try
        {
            var query = await ctx.Evidence.AppendAsync(
                new EvidenceEvent(ctx.SessionId, null, ctx.ApiKeyId,
                    "QuerySubmitted", "User", question, ""), ct);
            var retrieved = await ctx.Evidence.AppendAsync(
                new EvidenceEvent(ctx.SessionId, null, ctx.ApiKeyId,
                    "ChunksRetrieved", "System", question,
                    string.Join(',', chunks.Select(c => c.ChunkId)),
                    chunks.Select(c => c.ChunkText).ToList(),
                    [query]), ct);
            return await ctx.Evidence.AppendAsync(
                new EvidenceEvent(ctx.SessionId, null, ctx.ApiKeyId,
                    "AnswerSynthesized", "Agent", question, answer,
                    Parents: [retrieved]), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SPEC-20260929-observability-and-tests-residual RF-003: swallowed
            // emission failures still mark the span so they are observable.
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            ctx.Logger?.LogWarning(ex, "evidence emission failed for session {SessionId}", ctx.SessionId);
            return null;
        }
    }

    /// <summary>One ToolExecuted receipt chained to the previous step.</summary>
    public static async Task<EvidenceReceipt?> RecordToolAsync(
        EmissionContext ctx, string? threadId,
        string toolName, string? argsJson, string? resultJson,
        EvidenceReceipt? parent, CancellationToken ct)
    {
        if (ctx.Evidence is null)
            return null;
        using var span = Telemetry.KnowledgeHubActivity.Start("evidence.emit");
        span?.SetTag("kind", "tool");
        try
        {
            return await ctx.Evidence.AppendAsync(
                new EvidenceEvent(ctx.SessionId, threadId, ctx.ApiKeyId,
                    "ToolExecuted", "McpTool",
                    $"{toolName}({argsJson ?? ""})", resultJson ?? "",
                    Parents: parent is null ? null : [parent]), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            ctx.Logger?.LogWarning(ex, "evidence emission failed for tool {Tool}", toolName);
            return null;
        }
    }
}
