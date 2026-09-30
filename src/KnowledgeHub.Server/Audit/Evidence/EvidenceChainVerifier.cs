using System.Security.Cryptography;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Audit.Evidence;

/// <summary>One failed-node record in a verification report.</summary>
public sealed record EvidenceViolation(
    string ReceiptId,
    string Code,         // TamperingDetected|BrokenParentLink|BadSignature|OrderingViolation
    string Detail);

public sealed record EvidenceVerification(
    bool IsValid,
    IReadOnlyList<EvidenceViolation> Violations)
{
    public static readonly EvidenceVerification Empty = new(true, []);
}

/// <summary>
/// Offline integrity engine (RF-003): replays a receipt sequence and
/// recomputes digests, signatures (when the instance key is supplied) and
/// parent links. A single tampered byte surfaces as a precise node failure.
/// </summary>
public static class EvidenceChainVerifier
{
    /// <summary>Verifies <paramref name="receipts"/> — ordered by timestamp;
    /// <paramref name="hmacKey"/> (raw bytes) enables signature checks —
    /// without it digests+links are verified and signatures marked unchecked.</summary>
    public static EvidenceVerification Verify(
        IReadOnlyList<EvidenceReceipt> receipts, byte[]? hmacKey = null)
    {
        // SPEC-20260928-observability-followups RF-002: span per verification pass.
        using var span = Telemetry.KnowledgeHubActivity.Start("evidence.verify");
        span?.SetTag("receipts", receipts.Count);
        var violations = new List<EvidenceViolation>();
        var byId = receipts.ToDictionary(r => r.ReceiptId);
        var seen = new HashSet<string>();

        foreach (var r in receipts)
        {
            // 1. Recompute the canonical digest.
            CheckDigest(r, violations);
            // 2. Signature check (skipped only when the key is unavailable).
            CheckSignature(r, hmacKey, violations);
            // 3. Parent links must resolve to already-seen valid receipts.
            CheckParentLinks(r, byId, seen, violations);
            seen.Add(r.ReceiptId);
        }

        // 4. Ordering — timestamps must be non-decreasing for a clean chain.
        for (var i = 1; i < receipts.Count; i++)
            if (receipts[i].Timestamp < receipts[i - 1].Timestamp)
                violations.Add(new EvidenceViolation(receipts[i].ReceiptId,
                    "OrderingViolation", "timestamp precedes the previous receipt"));

        return new EvidenceVerification(violations.Count == 0, violations);
    }

    /// <summary>Step 1 — canonical digest must match the recomputation.</summary>
    private static void CheckDigest(
        EvidenceReceipt r, List<EvidenceViolation> violations)
    {
        if (!string.Equals(
            EvidenceChainService.ComputeDigest(r), r.ReceiptDigest,
            StringComparison.OrdinalIgnoreCase))
            violations.Add(new EvidenceViolation(r.ReceiptId, "TamperingDetected",
                "canonical digest mismatch — body fields were altered"));
    }

    /// <summary>Step 2 — SPEC-20260929 RF-005: an unrecognized scheme is a
    /// violation — swapping the prefix must not smuggle an unsigned receipt.</summary>
    private static void CheckSignature(
        EvidenceReceipt r, byte[]? hmacKey, List<EvidenceViolation> violations)
    {
        if (r.Signature is null
            || !r.Signature.StartsWith("hmac-sha256:", StringComparison.Ordinal))
        {
            if (hmacKey is not null)
                violations.Add(new EvidenceViolation(r.ReceiptId,
                    "UnknownSignatureScheme",
                    "signature is missing or uses an unknown scheme"));
            return;
        }
        if (hmacKey is null)
            return;

        using var hmac = new HMACSHA256(hmacKey);
        var expected = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(r.ReceiptDigest)))
            .ToLowerInvariant();
        if (!string.Equals(r.Signature, $"hmac-sha256:{expected}",
            StringComparison.OrdinalIgnoreCase))
            violations.Add(new EvidenceViolation(r.ReceiptId, "BadSignature",
                "signature does not match the stored digest"));
    }

    /// <summary>Step 3 — parent links must resolve to already-seen receipts
    /// and the chained parent digest must match.</summary>
    private static void CheckParentLinks(
        EvidenceReceipt r, Dictionary<string, EvidenceReceipt> byId,
        HashSet<string> seen, List<EvidenceViolation> violations)
    {
        if (r.ParentReceiptIds is not { } idsJson)
            return;

        var ids = System.Text.Json.JsonSerializer
            .Deserialize<List<string>>(idsJson) ?? [];
        var parentDigests = new List<string>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var parent) || !seen.Contains(id))
                violations.Add(new EvidenceViolation(r.ReceiptId, "BrokenParentLink",
                    $"parent '{id}' missing from bundle or appears out of order"));
            else
                parentDigests.Add(parent.ReceiptDigest);
        }
        var expectedParent = parentDigests.Count == 0
            ? "" : EvidenceChainService.Sha256Hex(string.Join(':', parentDigests));
        if (parentDigests.Count == ids.Count && ids.Count > 0
            && !string.Equals(r.ParentDigest, expectedParent,
                StringComparison.OrdinalIgnoreCase))
            violations.Add(new EvidenceViolation(r.ReceiptId, "TamperingDetected",
                "parent digest mismatch — parent receipts were replaced"));
    }
}
