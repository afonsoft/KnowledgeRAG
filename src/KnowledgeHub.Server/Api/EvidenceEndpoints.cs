using System.Text.Json;
using KnowledgeHub.Server.Audit.Evidence;
using KnowledgeHub.Server.Domain.Entities;
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
            // produced — every receipt must carry its ApiKeyId. Cookie callers
            // are refused outright: the shared "mcp:session" bucket carries no
            // per-user attribution, so no cookie-scoped ownership can be proven.
            var denied = await ExportDeniedAsync(receipts, http, ct);
            if (denied is not null)
                return denied;

            // SPEC-20260929 RF-003: integrity is re-verified with the instance
            // key — over exactly the exported snapshot, not a fresh re-read.
            var verification = await evidence.VerifyReceiptsAsync(receipts, ct);

            return Results.Ok(new
            {
                sessionId,
                generatedAt = DateTimeOffset.UtcNow,
                instanceKeyId = evidence.KeyId,
                signatureScheme = "hmac-sha256",
                totalReceipts = receipts.Count,
                chainIntegrity = verification.IsValid ? "Valid" : "Failed",
                violations = verification.Violations,
                receipts = receipts.Select(ProjectReceipt)
            });
        });

        return group;
    }

    /// <summary>RF-002 export authorization — non-null result means denied.</summary>
    private static async Task<IResult?> ExportDeniedAsync(
        IReadOnlyList<EvidenceReceipt> receipts, HttpContext http, CancellationToken ct)
    {
        if (http.RequestServices.GetService<Auth.ICallerScopeProvider>() is not { } provider)
            return null;
        var scope = await provider.GetAsync(ct);
        if (scope is null)
            return null;
        if (scope.ApiKeyId is null)
            return Results.Forbid();
        if (receipts.Any(r => !string.Equals(r.ApiKeyId,
                scope.ApiKeyId.Value.ToString("N"), StringComparison.OrdinalIgnoreCase)))
            return Results.Forbid();
        return null;
    }

    /// <summary>Wire projection of one receipt (null JSON blobs stay null).</summary>
    private static object ProjectReceipt(EvidenceReceipt r) => new
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
    };
}
