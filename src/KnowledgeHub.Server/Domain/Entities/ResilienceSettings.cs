namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// Single-row Resilience override (SPEC-20260928-resilience-tool-fallback-wiring
/// RF-004): fallback mode/budget/alternates editable from /settings without
/// redeploy. Absent row → the Resilience:Fallback configuration keys apply.
/// </summary>
public sealed class ResilienceSettings
{
    /// <summary>Single-row table — the service always upserts row Id = 1.</summary>
    public int Id { get; set; }
    /// <summary>disabled | observe | enforce.</summary>
    public string Mode { get; set; } = "disabled";
    /// <summary>Maximum alternate providers tried per request.</summary>
    public int MaxFallbackAttempts { get; set; } = 2;
    /// <summary>JSON array of ordered alternate chat providers ({endpoint,model,apiKey}).</summary>
    public string? ChatFallbacksJson { get; set; }
    /// <summary>JSON object: capability → ordered provider names.</summary>
    public string? ToolCapabilitiesJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
