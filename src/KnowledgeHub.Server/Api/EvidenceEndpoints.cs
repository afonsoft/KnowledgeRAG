using System.Text.Json;
using KnowledgeHub.Server.Audit.Evidence;
using Microsoft.AspNetCore.Mvc;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// Evidence bundle export (SPEC-20260927-cryptographic-evidence-provenance-chain
/// RF-004): <c>GET /api/v1/evidence/sessions/{sessionId}/bundle</c> returns the
/// full receipt set for a session plus integrity re-verification — consumable
/// offline by <see cref="EvidenceChainVerifier"/>.
/// </summary>
public static class EvidenceEndpoints
{
    public static RouteGroupBuilder MapEvidenceApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/evidence");

        group.MapGet("/sessions/{sessionId}/bundle", async (
            string sessionId,
            IEvidenceChainService evidence,
            CancellationToken ct) =>
        {
            var receipts = await evidence.GetSessionReceiptsAsync(sessionId, ct);
            var verification = EvidenceChainVerifier.Verify(receipts);

            return Results.Ok(new
            {
                sessionId,
                generatedAt = DateTimeOffset.UtcNow,
                instanceKeyId = evidence.KeyId,
                signatureScheme = "hmac-sha256",
                totalReceipts = receipts.Count,
                chainIntegrity = verification.IsValid ? "Valid" : "Failed",
                violations = verification.Violations,
                receipts = receipts.Select(r => new
                {
                    receiptId = r.ReceiptId,
                    parentReceiptIds = r.ParentReceiptIds is null
                        ? (object?)null : JsonSerializer.Deserialize<object>(r.ParentReceiptIds),
                    sessionId = r.SessionId,
                    threadId = r.ThreadId,
                    apiKeyId = r.ApiKeyId,
                    eventType = r.EventType,
                    actorType = r.ActorType,
                    inputHash = r.InputHash,
                    outputHash = r.OutputHash,
                    artifactHashes = r.ArtifactHashes is null
                        ? (object?)null : JsonSerializer.Deserialize<object>(r.ArtifactHashes),
                    timestamp = r.Timestamp,
                    parentDigest = r.ParentDigest,
                    receiptDigest = r.ReceiptDigest,
                    signature = r.Signature,
                    keyId = r.KeyId
                })
            });
        });

        return group;
    }
}
