using System.Text.Json;
using KnowledgeHub.Server.Caching;

namespace KnowledgeHub.Server.Auth;

/// <summary>
/// Resolved read scope of the calling principal
/// (SPEC-20260923-source-authorization RF-002): null sets mean unrestricted —
/// existing keys and cookie sessions keep working. An *empty* set is a
/// deny-all scope, deliberately distinct from null.
/// </summary>
public sealed record CallerScope(
    Guid? ApiKeyId,
    IReadOnlySet<Guid>? AllowedSourceIds,
    IReadOnlySet<string>? AllowedTools,
    bool AllowWrite = true)
{
    public static readonly CallerScope Unrestricted = new(null, null, null);

    public bool IsUnrestricted => AllowedSourceIds is null && AllowedTools is null;

    /// <summary>Stable cache-key segment: "*" when unrestricted so scoped and
    /// unscoped result payloads never share a key.</summary>
    public string SourceFingerprint =>
        AllowedSourceIds is null
            ? "*"
            : CacheKeys.Hash(string.Join(',', AllowedSourceIds.OrderBy(g => g)));

    /// <summary>Stable cache-key segment covering BOTH filter sets — payloads
    /// whose shape depends on the scope (tool lists) key on this so callers
    /// with different scopes never share a cached entry.</summary>
    public string Fingerprint =>
        IsUnrestricted
            ? "*"
            : CacheKeys.Hash($"{SourceFingerprint}|{ToolsFingerprint}");

    /// <summary>Stable cache-key segment for <see cref="AllowedTools"/>.</summary>
    public string ToolsFingerprint =>
        AllowedTools is null
            ? "*"
            : CacheKeys.Hash(string.Join(',', AllowedTools.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));

    public bool AllowsSource(Guid sourceId) =>
        AllowedSourceIds is null || AllowedSourceIds.Contains(sourceId);

    public bool AllowsTool(string name) =>
        AllowedTools is null || AllowedTools.Contains(name);

    /// <summary>Parses the stored JSON columns; malformed payloads fall back to
    /// unrestricted rather than locking the key out silently.
    /// <paramref name="allowWrite"/> gates non-readonly tools at call time —
    /// denied calls answer a friendly isError instead of executing.</summary>
    public static CallerScope FromJson(Guid apiKeyId, string? sourceIdsJson, string? toolsJson, bool allowWrite = true) =>
        new(apiKeyId, ParseGuidSet(sourceIdsJson), ParseStringSet(toolsJson), allowWrite);

    private static IReadOnlySet<Guid>? ParseGuidSet(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json)?.ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlySet<string>? ParseStringSet(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
