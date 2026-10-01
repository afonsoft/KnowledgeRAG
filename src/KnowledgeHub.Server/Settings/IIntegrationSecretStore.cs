namespace KnowledgeHub.Server.Settings;

/// <summary>Well-known provider slugs for the integration secret store.</summary>
public static class IntegrationProviders
{
    public const string Firecrawl = "firecrawl";
    public const string DeepWiki = "deepwiki";
    public const string Tavily = "tavily";
    public const string Context7 = "context7";
    /// <summary>Chat API key slug (SPEC-20260916-settings-chat-config) — deliberately
    /// kept out of <see cref="All"/>: it is managed by the /api/settings/chat
    /// endpoints, never listed in the integrations grid.</summary>
    public const string Chat = "chat";

    /// <summary>Embeddings API key slug (SPEC-20260926-settings-ux-embeddings) —
    /// same treatment as <see cref="Chat"/>: managed by /api/settings/embeddings,
    /// never listed in the integrations grid.</summary>
    public const string Embeddings = "embeddings";

    /// <summary>Assistant API key slug (SPEC-20260929-a2a-assistant-delegation) —
    /// same treatment as <see cref="Chat"/>: managed by /api/settings/assistant,
    /// never listed in the integrations grid.</summary>
    public const string Assistant = "assistant";

    public static readonly IReadOnlyList<string> All = [Firecrawl, DeepWiki, Tavily, Context7];
}

/// <summary>
/// Reversible, encrypted-at-rest store for upstream integration credentials
/// (SPEC-20260916-firecrawl-mcp-proxy RF-004). Secrets are protected with
/// ASP.NET Core Data Protection before hitting SQLite; reads never expose
/// more than the stored last-4 hint.
/// </summary>
public interface IIntegrationSecretStore
{
    /// <summary>Decrypted secret for <paramref name="provider"/>, or null when
    /// not stored. Returns null (with a logged warning) if the DB is
    /// unavailable, so env/config fallbacks still apply.</summary>
    Task<string?> GetAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>Display metadata — never the secret itself.</summary>
    Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>Encrypts and upserts the secret; records the last-4 hint.</summary>
    Task SetAsync(string provider, string secret, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored secret. Returns true when one existed.</summary>
    Task<bool> RemoveAsync(string provider, CancellationToken cancellationToken = default);
}

/// <summary>Masked view of a stored integration secret.</summary>
public sealed record IntegrationSecretInfo(string Provider, string KeyHint, DateTimeOffset UpdatedAt);

public static class IntegrationSecretStoreExtensions
{
    /// <summary>Effective secret for <paramref name="provider"/>: the store wins,
    /// then <paramref name="configured"/> (env/options) as fallback. Null when neither exists.</summary>
    public static async Task<string?> GetEffectiveAsync(
        this IIntegrationSecretStore store, string provider, string? configured, CancellationToken cancellationToken = default)
    {
        var stored = await store.GetAsync(provider, cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    /// <summary>Tri-state key status for settings DTOs: whether a key exists, its
    /// last-4 hint (store hint or tail of <paramref name="configured"/>), and the
    /// source ("store" | "env" | "none"). Callers apply their own mask prefix.</summary>
    public static async Task<(bool HasKey, string? Last4, string Source)> GetKeyStatusAsync(
        this IIntegrationSecretStore store, string provider, string? configured, CancellationToken cancellationToken = default)
    {
        var info = await store.GetInfoAsync(provider, cancellationToken);
        if (info is not null)
            return (true, info.KeyHint, "store");
        if (!string.IsNullOrWhiteSpace(configured))
            return (true, configured.Length >= 4 ? configured[^4..] : configured, "env");
        return (false, null, "none");
    }
}
