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
    /// <summary>QuerySubmitted → ChunksRetrieved → AnswerSynthesized chain.</summary>
    public static async Task<EvidenceReceipt?> RecordAskAsync(
        IEvidenceChainService? evidence,
        ILogger? logger,
        string sessionId, string? apiKeyId, string question,
        IReadOnlyList<SearchResultItem> chunks, string answer,
        CancellationToken ct)
    {
        if (evidence is null)
            return null;
        // SPEC-20260928-observability-followups RF-002: span per emission chain
        // (QuerySubmitted → ChunksRetrieved → AnswerSynthesized).
        using var span = Telemetry.KnowledgeHubActivity.Start("evidence.emit");
        span?.SetTag("kind", "ask");
        try
        {
            var query = await evidence.AppendAsync(
                new EvidenceEvent(sessionId, null, apiKeyId,
                    "QuerySubmitted", "User", question, ""), ct);
            var retrieved = await evidence.AppendAsync(
                new EvidenceEvent(sessionId, null, apiKeyId,
                    "ChunksRetrieved", "System", question,
                    string.Join(',', chunks.Select(c => c.ChunkId)),
                    chunks.Select(c => c.ChunkText).ToList(),
                    [query]), ct);
            return await evidence.AppendAsync(
                new EvidenceEvent(sessionId, null, apiKeyId,
                    "AnswerSynthesized", "Agent", question, answer,
                    Parents: [retrieved]), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SPEC-20260929-observability-and-tests-residual RF-003: swallowed
            // emission failures still mark the span so they are observable.
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            logger?.LogWarning(ex, "evidence emission failed for session {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>One ToolExecuted receipt chained to the previous step.</summary>
    public static async Task<EvidenceReceipt?> RecordToolAsync(
        IEvidenceChainService? evidence,
        ILogger? logger,
        string sessionId, string? apiKeyId, string? threadId,
        string toolName, string callId, string? argsJson, string? resultJson,
        EvidenceReceipt? parent, CancellationToken ct)
    {
        if (evidence is null)
            return null;
        using var span = Telemetry.KnowledgeHubActivity.Start("evidence.emit");
        span?.SetTag("kind", "tool");
        try
        {
            return await evidence.AppendAsync(
                new EvidenceEvent(sessionId, threadId, apiKeyId,
                    "ToolExecuted", "McpTool",
                    $"{toolName}({argsJson ?? ""})", resultJson ?? "",
                    Parents: parent is null ? null : [parent]), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            logger?.LogWarning(ex, "evidence emission failed for tool {Tool}", toolName);
            return null;
        }
    }
}
