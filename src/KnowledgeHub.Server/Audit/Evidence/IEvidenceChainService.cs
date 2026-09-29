using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Audit.Evidence;

/// <summary>Event material for one append (service computes all digests).</summary>
public sealed record EvidenceEvent(
    string SessionId,
    string? ThreadId,
    string? ApiKeyId,
    string EventType,          // QuerySubmitted|ChunksRetrieved|ToolExecuted|AnswerSynthesized
    string ActorType,          // User|Agent|System|McpTool
    string InputPayload,
    string OutputPayload,
    IReadOnlyList<string>? ArtifactPayloads = null,
    IReadOnlyList<EvidenceReceipt>? Parents = null);

/// <summary>
/// Append-only receipt log (RF-001/RF-002): canonical digests, parent
/// chaining, instance-key signature. Emission is best-effort at call sites —
/// callers wrap in try/catch so a store hiccup never breaks the RAG flow.
/// </summary>
public interface IEvidenceChainService
{
    Task<EvidenceReceipt> AppendAsync(EvidenceEvent ev, CancellationToken ct);

    /// <summary>All receipts for one session, oldest first.</summary>
    Task<IReadOnlyList<EvidenceReceipt>> GetSessionReceiptsAsync(
        string sessionId, CancellationToken ct);

    /// <summary>Recomputes digests + verifies signatures of stored receipts —
    /// used by the bundle endpoint to report integrity on export.</summary>
    string KeyId { get; }

    /// <summary>SPEC-20260929 RF-003: full verification with the instance
    /// signing key — the export path must re-verify, not trust stored
    /// digests.</summary>
    Task<EvidenceVerification> VerifyAsync(string sessionId, CancellationToken ct);
}
