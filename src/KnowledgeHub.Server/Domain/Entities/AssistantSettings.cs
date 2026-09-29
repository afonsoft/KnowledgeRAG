namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// Single-row assistant-provider override (SPEC-20260929-a2a-assistant-delegation
/// RF-001): the low-cost "assistant" model that executes cheap sub-tasks
/// (rewrite, grading, summarization) so the main model stays reserved for
/// synthesis/tool-calling. The API key is NOT stored here — it lives in
/// <see cref="IntegrationSecret"/> under the "assistant" slug.
/// </summary>
public sealed class AssistantSettings : Settings.ISingleRowSettings
{
    /// <summary>Single-row table — the service always upserts row Id = 1.</summary>
    public int Id { get; set; }
    /// <summary>Master switch — false = sub-tasks use the main chat model.</summary>
    public bool Enabled { get; set; }
    /// <summary>"local" (OpenAI-compatible endpoint) | "remote" (A2A agent).</summary>
    public string Mode { get; set; } = "local";
    /// <summary>Local: OpenAI-compatible base URL. Remote: A2A Agent Card base URL.</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>Local mode only — model name sent to the provider.</summary>
    public string? Model { get; set; }
    /// <summary>JSON array of routed sub-task names (rewrite/grade/expand/summarize).</summary>
    public string? RouteJson { get; set; }
    /// <summary>Per-call timeout before falling back to the main model.</summary>
    public int TimeoutSeconds { get; set; } = 15;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
