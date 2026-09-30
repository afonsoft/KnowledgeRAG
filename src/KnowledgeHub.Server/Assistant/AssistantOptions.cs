namespace KnowledgeHub.Server.Assistant;

/// <summary>
/// Configuration for the low-cost assistant provider (SPEC-20260929-a2a-assistant-
/// delegation RF-001/RF-002). Disabled by default; the settings row overrides
/// these env/config values without restart.
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Master switch — false = every sub-task uses the main chat model.</summary>
    public bool Enabled { get; set; }

    /// <summary>"local" (OpenAI-compatible endpoint) | "remote" (A2A agent card URL).</summary>
    public string Mode { get; set; } = "local";

    /// <summary>Local: OpenAI-compatible base URL. Remote: A2A agent base URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Local mode only — model name sent to the provider.</summary>
    public string? Model { get; set; }

    /// <summary>Bearer key (local provider or remote A2A agent). Env var only — never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Sub-tasks routed to the assistant (rewrite | grade | expand | summarize).</summary>
    public string[] Route { get; set; } = ["rewrite", "grade", "summarize"];

    /// <summary>Per-call timeout before falling back to the main model.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
