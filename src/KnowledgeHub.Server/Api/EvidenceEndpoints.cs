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
            HttpContext http,
            CancellationToken ct) =>
        {
            var receipts = await evidence.GetSessionReceiptsAsync(sessionId, ct);

            // SPEC-20260929 RF-002: an aft_* caller may only export sessions it
            // produced — every receipt must carry its ApiKeyId (cookie-owned
            // sessions are equally off-limits to API keys).
            if (http.RequestServices.GetService<Auth.ICallerScopeProvider>() is { } scopeProvider
                && (await scopeProvider.GetAsync(ct)).ApiKeyId is { } callerKey
                && receipts.Any(r => !string.Equals(r.ApiKeyId,
                    callerKey.ToString("N"), StringComparison.OrdinalIgnoreCase)))
                return Results.Forbid();

            // SPEC-20260929 RF-003: integrity is re-verified with the instance
            // key — never trusted from stored digests alone.
            var verification = await evidence.VerifyAsync(sessionId, ct);

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
