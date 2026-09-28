using System.Text.Json.Nodes;
using KnowledgeHub.Server.Audit.Evidence;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Audit;

// SPEC-20260927-cryptographic-evidence-provenance-chain: canonical hashing,
// append chain, tamper detection, broken parent links and signatures.
public sealed class EvidenceChainTests
{
    private sealed class MemSecrets : IIntegrationSecretStore
    {
        public readonly Dictionary<string, string> Store = new();
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Store.GetValueOrDefault(provider));
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default)
        {
            Store[provider] = secret;
            return Task.CompletedTask;
        }
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Store.Remove(provider));
    }

    private static async Task<(KnowledgeHubDbContext Db, EvidenceChainService Service, MemSecrets Secrets)>
        CreateAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite(conn).Options;
        var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var secrets = new MemSecrets();
        return (db, new EvidenceChainService(db, secrets,
            NullLogger<EvidenceChainService>.Instance), secrets);
    }

    // RF-001: canonical serializer — key order + no whitespace differences.
    [Fact]
    public void CanonicalJson_KeyOrderDeterministic()
    {
        var a = JsonNode.Parse("""{"b":1,"a":{"z":2,"y":1}}""");
        var b = JsonNode.Parse("""{"a":{"y":1,"z":2},"b":1}""");
        var x = CanonicalJsonSerializer.Serialize(a);
        var y = CanonicalJsonSerializer.Serialize(b);
        Assert.Equal(x, y);
        Assert.Equal("""{"a":{"y":1,"z":2},"b":1}""", x);
    }

    // AC-1: ask flow emits a signed chained AnswerSynthesized receipt.
    [Fact]
    public async Task AppendChain_LinksParentsAndSigns()
    {
        var (_, svc, secrets) = await CreateAsync();
        var q = await svc.AppendAsync(new EvidenceEvent("s1", null, "k1",
            "QuerySubmitted", "User", "what?", ""), CancellationToken.None);
        var c = await svc.AppendAsync(new EvidenceEvent("s1", null, "k1",
            "ChunksRetrieved", "System", "what?", "c1,c2",
            ["chunk one", "chunk two"], [q]), CancellationToken.None);
        var a = await svc.AppendAsync(new EvidenceEvent("s1", null, "k1",
            "AnswerSynthesized", "Agent", "what?", "answer text", Parents: [c]),
            CancellationToken.None);

        Assert.Equal(64, a.ReceiptDigest.Length);
        Assert.StartsWith("hmac-sha256:", a.Signature);
        Assert.Contains(q.ReceiptId, c.ParentReceiptIds);
        Assert.Contains(c.ReceiptId, a.ParentReceiptIds);
        Assert.Equal(
            EvidenceChainService.Sha256Hex(string.Join(':', new[] { c.ReceiptDigest })),
            a.ParentDigest);
        // Signing key persisted, never in config/logs.
        Assert.True(secrets.Store.ContainsKey(EvidenceChainService.SecretSlot));
    }

    // AC-2: intact chain verifies clean.
    [Fact]
    public async Task Verify_IntactChainIsValid()
    {
        var (_, svc, secrets) = await CreateAsync();
        var q = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "QuerySubmitted", "User", "q", ""), CancellationToken.None);
        var c = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "ChunksRetrieved", "System", "q", "chunks", ["t1"], [q]), CancellationToken.None);
        var a = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "AnswerSynthesized", "Agent", "q", "a", Parents: [c]), CancellationToken.None);

        var key = Convert.FromHexString(secrets.Store[EvidenceChainService.SecretSlot]);
        var v = EvidenceChainVerifier.Verify([q, c, a], key);
        Assert.True(v.IsValid);
        Assert.Empty(v.Violations);
    }

    // AC-3: tampered field → TamperingDetected on that node.
    [Fact]
    public async Task Verify_TamperedReceiptDetected()
    {
        var (_, svc, _) = await CreateAsync();
        var q = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "QuerySubmitted", "User", "q", ""), CancellationToken.None);
        var a = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "AnswerSynthesized", "Agent", "q", "legit answer", Parents: [q]),
            CancellationToken.None);
        a.OutputHash = new string('0', 64); // manual DB tampering simulation

        var v = EvidenceChainVerifier.Verify([q, a]);
        Assert.False(v.IsValid);
        Assert.Contains(v.Violations, x => x.ReceiptId == a.ReceiptId
            && x.Code == "TamperingDetected");
    }

    // AC-4: removed intermediate receipt → BrokenParentLink.
    [Fact]
    public async Task Verify_MissingParentDetected()
    {
        var (_, svc, _) = await CreateAsync();
        var q = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "QuerySubmitted", "User", "q", ""), CancellationToken.None);
        var c = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "ChunksRetrieved", "System", "q", "x", ["t"], [q]), CancellationToken.None);
        var a = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "AnswerSynthesized", "Agent", "q", "a", Parents: [c]), CancellationToken.None);

        // Drop the intermediate ChunksRetrieved receipt.
        var v = EvidenceChainVerifier.Verify([q, a]);
        Assert.False(v.IsValid);
        Assert.Contains(v.Violations, x => x.Code == "BrokenParentLink");
    }

    // Edge: empty bundle verifies vacuously.
    [Fact]
    public void Verify_EmptyBundleIsValid()
    {
        var v = EvidenceChainVerifier.Verify([]);
        Assert.True(v.IsValid);
    }

    // Edge: bad signature rejected when key is supplied.
    [Fact]
    public async Task Verify_BadSignatureDetected()
    {
        var (_, svc, secrets) = await CreateAsync();
        var r = await svc.AppendAsync(new EvidenceEvent("s1", null, null,
            "QuerySubmitted", "User", "q", ""), CancellationToken.None);
        r.Signature = $"hmac-sha256:{new string('f', 64)}";
        var key = Convert.FromHexString(secrets.Store[EvidenceChainService.SecretSlot]);
        var v = EvidenceChainVerifier.Verify([r], key);
        Assert.False(v.IsValid);
        Assert.Contains(v.Violations, x => x.Code == "BadSignature");
    }

    // Empty payload → sha256 of "" (e3b0c44...).
    [Fact]
    public void Sha256_EmptyString()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            EvidenceChainService.Sha256Hex(""));
    }
}
