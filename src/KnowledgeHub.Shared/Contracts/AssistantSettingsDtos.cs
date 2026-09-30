namespace KnowledgeHub.Shared.Contracts;

/// <summary>Effective assistant-provider state for the Settings UI — never
/// carries the API key itself (SPEC-20260929-a2a-assistant-delegation RF-001).</summary>
public sealed record AssistantSettingsDto
{
    /// <summary>Whether the assistant is routed for any sub-task.</summary>
    public required bool Enabled { get; init; }
    /// <summary>"local" (OpenAI-compatible) | "remote" (A2A agent).</summary>
    public required string Mode { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    /// <summary>Effective routed sub-task names.</summary>
    public required string[] Route { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required bool HasApiKey { get; init; }
    public string? ApiKeyHint { get; init; }
    /// <summary>"store" | "env" | "none".</summary>
    public required string ApiKeySource { get; init; }
    /// <summary>Where the row comes from: "store" | "env" | "none".</summary>
    public required string Source { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>PUT /api/settings/assistant body. Blank <see cref="ApiKey"/> keeps
/// the stored key. Null <see cref="Route"/> keeps the default route set.</summary>
public sealed record SaveAssistantSettingsRequest
{
    public required bool Enabled { get; init; }
    public required string Mode { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string[]? Route { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? ApiKey { get; init; }
}

/// <summary>POST /api/settings/assistant/test body — blanks fall back to the
/// effective configuration.</summary>
public sealed record TestAssistantConnectionRequest
{
    public string? Mode { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
}
