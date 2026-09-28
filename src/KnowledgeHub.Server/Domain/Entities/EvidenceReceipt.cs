namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// Cryptographic evidence receipt — append-only (SPEC-20260927
/// cryptographic-evidence-provenance-chain RF-001). Every field except
/// <see cref="Signature"/> participates in <see cref="ReceiptDigest"/>.
/// UPDATE/DELETE on this table is forbidden by contract.
/// </summary>
public sealed class EvidenceReceipt
{
    /// <summary>Time-sortable id: <c>rec_{ticks:x}_{guid}</c>.</summary>
    public string ReceiptId { get; set; } = "";
    public string? ParentReceiptIds { get; set; }   // JSON array
    public string SessionId { get; set; } = "";
    public string? ThreadId { get; set; }
    public string? ApiKeyId { get; set; }
    public string EventType { get; set; } = "";     // QuerySubmitted|ChunksRetrieved|ToolExecuted|AnswerSynthesized
    public string ActorType { get; set; } = "";     // User|Agent|System|McpTool
    public string InputHash { get; set; } = "";     // sha256 hex (empty-string hash when absent)
    public string OutputHash { get; set; } = "";
    public string? ArtifactHashes { get; set; }     // JSON array of sha256 hex
    public DateTimeOffset Timestamp { get; set; }
    public string ParentDigest { get; set; } = "";  // sha256 over parent digests
    public string ReceiptDigest { get; set; } = ""; // sha256 over canonical body
    public string Signature { get; set; } = "";     // "hmac-sha256:{hex}"
    public string KeyId { get; set; } = "khub-master";
}
