using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Audit.Evidence;

/// <summary>
/// Builds, signs and persists receipts (RF-001/RF-002). Signing key lives in
/// the encrypted secret store under <c>evidence:master</c> — generated once
/// (32 random bytes) so receipts stay verifiable across restarts; the key is
/// never logged nor returned by any API.
/// </summary>
public sealed class EvidenceChainService(
    KnowledgeHubDbContext db,
    IIntegrationSecretStore secrets,
    ILogger<EvidenceChainService> logger) : IEvidenceChainService
{
    public const string SecretSlot = "evidence:master";
    public string KeyId { get; } = "khub-master";

    private byte[]? _key;

    public static string Sha256Hex(string? payload) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? "")))
            .ToLowerInvariant();

    public async Task<EvidenceReceipt> AppendAsync(EvidenceEvent ev, CancellationToken ct)
    {
        var receipt = new EvidenceReceipt
        {
            ReceiptId = NewId(),
            SessionId = ev.SessionId,
            ThreadId = ev.ThreadId,
            ApiKeyId = ev.ApiKeyId,
            EventType = ev.EventType,
            ActorType = ev.ActorType,
            InputHash = Sha256Hex(ev.InputPayload),
            OutputHash = Sha256Hex(ev.OutputPayload),
            ArtifactHashes = ev.ArtifactPayloads is { Count: > 0 }
                ? JsonSerializer.Serialize(ev.ArtifactPayloads.Select(Sha256Hex))
                : null,
            Timestamp = DateTimeOffset.UtcNow
        };

        if (ev.Parents is { Count: > 0 } parents)
        {
            receipt.ParentReceiptIds =
                JsonSerializer.Serialize(parents.Select(p => p.ReceiptId));
            // ParentDigest binds the parents' digests — a swap of parent rows
            // breaks verification even when ids are preserved.
            receipt.ParentDigest = Sha256Hex(
                string.Join(':', parents.Select(p => p.ReceiptDigest)));
        }

        receipt.ReceiptDigest = ComputeDigest(receipt);
        receipt.KeyId = KeyId;
        receipt.Signature = $"hmac-sha256:{await SignAsync(receipt.ReceiptDigest, ct)}";

        db.EvidenceReceipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task<IReadOnlyList<EvidenceReceipt>> GetSessionReceiptsAsync(
        string sessionId, CancellationToken ct) =>
        await db.EvidenceReceipts
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.Timestamp).ThenBy(r => r.ReceiptId)
            .ToListAsync(ct);

    /// <summary>Canonical digest over the signed fields (signature excluded).</summary>
    public static string ComputeDigest(EvidenceReceipt r)
    {
        var body = new JsonObject
        {
            ["actorType"] = r.ActorType,
            ["artifactHashes"] = r.ArtifactHashes is null
                ? null : JsonNode.Parse(r.ArtifactHashes),
            ["apiKeyId"] = r.ApiKeyId,
            ["eventType"] = r.EventType,
            ["inputHash"] = r.InputHash,
            ["outputHash"] = r.OutputHash,
            ["parentDigest"] = r.ParentDigest,
            ["parentReceiptIds"] = r.ParentReceiptIds is null
                ? null : JsonNode.Parse(r.ParentReceiptIds),
            ["receiptId"] = r.ReceiptId,
            ["sessionId"] = r.SessionId,
            ["threadId"] = r.ThreadId,
            ["timestamp"] = r.Timestamp.UtcDateTime.ToString("O")
        };
        return Sha256Hex(CanonicalJsonSerializer.Serialize(body));
    }

    /// <summary>Time-sortable receipt id — lexical order ≈ creation order.</summary>
    private static string NewId() =>
        $"rec_{DateTimeOffset.UtcNow.Ticks:x16}_{Guid.NewGuid():N}";

    private async Task<string> SignAsync(string digest, CancellationToken ct)
    {
        _key ??= await LoadOrCreateKeyAsync(ct);
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(digest))).ToLowerInvariant();
    }

    private async Task<byte[]> LoadOrCreateKeyAsync(CancellationToken ct)
    {
        var stored = await secrets.GetAsync(SecretSlot, ct);
        if (stored is { Length: > 0 })
            return Convert.FromHexString(stored);
        var key = RandomNumberGenerator.GetBytes(32);
        await secrets.SetAsync(SecretSlot, Convert.ToHexString(key), ct);
        logger.LogInformation("evidence: instance signing key generated (keyId={KeyId})", KeyId);
        return key;
    }
}
