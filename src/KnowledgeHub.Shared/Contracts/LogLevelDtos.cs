namespace KnowledgeHub.Shared.Contracts;

/// <summary>PUT /api/settings/log-level body (SPEC-20260925-runtime-log-level).</summary>
public sealed record SetLogLevelRequest(string? Level, int? Minutes);

/// <summary>GET/PUT /api/settings/log-level payload (SPEC-20260925-runtime-log-level).</summary>
public sealed class LogLevelState
{
    public string Level { get; set; } = "Information";
    public string? ConfiguredDefault { get; set; }
    public DateTimeOffset? AutoResetAt { get; set; }
}
