using System.Security.Cryptography;
using System.Text;

namespace KnowledgeHub.Server.Memory;

/// <summary>SPEC-20261111-agent-memory-retain RF-001/RF-005: content hashing.</summary>
public static class MemoryHasher
{
    /// <summary>SHA-256 hex of whitespace-normalized text (case preserved —
    /// dedupe is byte-exact modulo whitespace, not fuzzy).</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Normalize(text)))).ToLowerInvariant();

    public static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
