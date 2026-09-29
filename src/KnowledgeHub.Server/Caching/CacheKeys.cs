using System.Security.Cryptography;
using System.Text;

namespace KnowledgeHub.Server.Caching;

/// <summary>Key conventions + index-version token for the distributed cache
/// (SPEC-20260916-performance-memory-cache §5).</summary>
public static class CacheKeys
{
    /// <summary>Opaque token bumped by ingestion on every sync; search-result
    /// keys embed it so index changes invalidate stale results immediately.</summary>
    public const string IndexVersion = "index:version";

    /// <summary>SPEC-20260926-embeddings-runtime-coherence RF-004: the provider
    /// signature fingerprint is part of the key — same ModelId over a different
    /// endpoint/key/input_type never reuses another provider's vectors.</summary>
    public static string Embedding(string modelId, string fingerprint, string text) =>
        $"emb:{modelId}:{fingerprint}:{Sha256(text)}";

    /// <summary>SPEC-20260923-retrieval-quality RF-005: v2 embedded the filter
    /// fingerprint; SPEC-20260923-source-authorization RF-003 bumps to v3 adding
    /// the caller's source-scope fingerprint so keys with different scopes
    /// never share cached results.</summary>
    public static string Search(string mode, int topK, Guid? sourceId, string filterFingerprint, string scopeFingerprint, string query, string indexVersion) =>
        $"search:v3:{mode}:{topK}:{sourceId?.ToString("N") ?? "all"}:{Sha256(filterFingerprint)}:{scopeFingerprint}:{Sha256(query)}:v{indexVersion}";

    /// <summary>SPEC-20260923-agent-runtime-hardening RF-003: answer cache key.
    /// The ordered context fingerprints (chunk ids for indexed content,
    /// content hashes for id-less items such as live-tool outputs) identify
    /// the retrieval exactly; indexVersion invalidates on every sync.
    /// SPEC-20260929-live-actions-bridge-hardening RF-003: live executions
    /// carry no chunk id — their content hash includes the fresh output, so
    /// a cached answer is never reused after new live data arrives.</summary>
    public static string Answer(string model, string question, IEnumerable<string> contextFingerprints, string indexVersion) =>
        $"ans:{model}:{Sha256(question)}:{Sha256(string.Join(',', contextFingerprints))}:v{indexVersion}";

    /// <summary>SPEC-20260924-redis-cache-and-tool-caching: tool invocation cache key.
    /// Hashes tool name + arguments + indexVersion so ingestion bumps invalidate tool results.</summary>
    public static string Tool(string toolName, string argumentsJson, string indexVersion) =>
        $"mcp:tool:{toolName}:{Sha256(argumentsJson)}:v{indexVersion}";

    /// <summary>Short stable content hash for cache keys.</summary>
    public static string Hash(string value) => Sha256(value);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
