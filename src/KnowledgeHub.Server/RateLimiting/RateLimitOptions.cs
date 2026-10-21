namespace KnowledgeHub.Server.RateLimiting;

/// <summary>
/// SPEC-20260923-rate-limiting — <c>RateLimiting:*</c> configuration.
/// Limits are per-partition (api key → user → ip); there is no global cap.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Master switch — false disables every limiter (HTTP + MCP).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Authenticated permits for the <c>llm</c> policy (ask/agent/LLM tools).</summary>
    public int LlmPermitLimit { get; set; } = 20;

    /// <summary>Sliding window for the <c>llm</c> policy.</summary>
    public int LlmWindowSeconds { get; set; } = 60;

    /// <summary>Stricter bucket for unauthenticated callers on <c>llm</c>.</summary>
    public int AnonymousLlmPermitLimit { get; set; } = 5;

    /// <summary>Permits for the <c>sync</c> policy (source sync / write tools).</summary>
    public int SyncPermitLimit { get; set; } = 10;

    /// <summary>Fixed window for the <c>sync</c> policy.</summary>
    public int SyncWindowSeconds { get; set; } = 3600;

    /// <summary>Permits for the <c>context</c> MCP tool bucket
    /// (context_save/recovery/compact — SPEC-20261004-context-snapshots RF-009).</summary>
    public int ContextPermitLimit { get; set; } = 60;

    /// <summary>Sliding window for the <c>context</c> bucket.</summary>
    public int ContextWindowSeconds { get; set; } = 60;

    /// <summary>Permits for the <c>general</c> policy (remaining /api/*).</summary>
    public int GeneralPermitLimit { get; set; } = 300;

    /// <summary>Fixed window for the <c>general</c> policy.</summary>
    public int GeneralWindowSeconds { get; set; } = 60;

    /// <summary>Trust X-Forwarded-For for IP partitioning (spoofable — opt-in).</summary>
    public bool TrustForwardedHeaders { get; set; }
}
