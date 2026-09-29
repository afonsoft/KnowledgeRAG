namespace KnowledgeHub.Shared.Contracts;

/// <summary>Effective resilience/fallback settings for the Settings UI
/// (SPEC-20260928-resilience-tool-fallback-wiring RF-004). Secrets are never
/// returned — chat fallback apiKey fields are write-only.</summary>
public sealed record ResilienceSettingsDto
{
    /// <summary>disabled | observe | enforce.</summary>
    public required string Mode { get; init; }
    /// <summary>Maximum alternate providers tried per request (1–5).</summary>
    public required int MaxFallbackAttempts { get; init; }
    /// <summary>Ordered alternate chat providers; <c>apiKey</c> arrives masked.</summary>
    public required IReadOnlyList<ChatFallbackOptionDto> ChatFallbacks { get; init; }
    /// <summary>Capability → ordered provider names (tool fallback chains).</summary>
    public required IReadOnlyDictionary<string, List<string>> ToolCapabilities { get; init; }
    /// <summary>Where the effective values come from: "store" | "env".</summary>
    public required string Source { get; init; }
    /// <summary>True when env/config supplies Resilience:Fallback keys.</summary>
    public required bool EnvConfigured { get; init; }
    /// <summary>Last update of the stored override, null when Source != store.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>One chat fallback provider (same shape as Chat options).</summary>
public sealed record ChatFallbackOptionDto
{
    public string? Provider { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    /// <summary>Masked on read (*** when set); write-only on save.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>PUT /api/settings/resilience body — validated server-side.</summary>
public sealed record SaveResilienceSettingsRequest
{
    public required string Mode { get; init; }
    public required int MaxFallbackAttempts { get; init; }
    public List<ChatFallbackOptionDto>? ChatFallbacks { get; init; }
    public Dictionary<string, List<string>>? ToolCapabilities { get; init; }
}
