using System.Security.Cryptography;
using System.Text;

namespace KnowledgeHub.Server.Context;

/// <summary>
/// Versioned, repo-scoped cache keys (SPEC-20261004-context-snapshots
/// RF-007/D-1). Context snapshots use a dedicated <c>ctx:*</c> HybridCache
/// layer — the generic <c>IToolCacheService</c> cannot be used because its
/// <c>indexVersion</c> component never changes on <c>context_save</c> (stale
/// <c>latest</c> reads) and it reports no hit flag for <c>stats.cacheHit</c>.
/// </summary>
public static class ContextCacheKeys
{
    public const string SchemaVersion = "v1";

    /// <summary>Invalidation tag — every entry for the repo carries it;
    /// <c>context_save</c> removes by tag.</summary>
    public static string RepoTag(string repoKey) => $"ctx:{repoKey}";

    /// <summary><c>ctx:v1:{repo}:{version|latest|label:X}:{strategy}:{budget}</c></summary>
    public static string Recovery(string repoKey, string versionSelector, string strategy, int tokenBudget) =>
        $"ctx:{SchemaVersion}:{repoKey}:{versionSelector}:{strategy}:{tokenBudget}";

    /// <summary><c>ctx:v1:{repo}:cmp:{sha256(canonical)[..32]}:{strategy}:{budget}</c> —
    /// raw content never enters the key, only its hash.</summary>
    public static string Compact(string repoKey, string canonicalItemsJson, string strategy, int tokenBudget)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalItemsJson)));
        return $"ctx:{SchemaVersion}:{repoKey}:cmp:{hash[..32]}:{strategy}:{tokenBudget}";
    }

    /// <summary>Pointer avoiding the latest-version query (TTL 1 h, refreshed on save).</summary>
    public static string LatestVersionPointer(string repoKey) =>
        $"ctx:{SchemaVersion}:{repoKey}:latestVersion";
}
