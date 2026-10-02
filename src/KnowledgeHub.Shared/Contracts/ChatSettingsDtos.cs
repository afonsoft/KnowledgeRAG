namespace KnowledgeHub.Shared.Contracts;

/// <summary>Effective chat-provider state for the Settings UI — never carries
/// the API key itself (SPEC-20260916-settings-chat-config RF-004).</summary>
public record ChatSettingsDto
{
    /// <summary>Effective provider: "openai" | "ollama" | "none".</summary>
    public required string Provider { get; init; }
    /// <summary>Effective endpoint (store value when Source=store, env value when env).</summary>
    public string? Endpoint { get; init; }
    /// <summary>Effective model.</summary>
    public string? Model { get; init; }
    /// <summary>True when an effective API key exists (store or env/config).</summary>
    public required bool HasApiKey { get; init; }
    /// <summary>Masked last-4 hint (e.g. "••••315f"), null when no key.</summary>
    public string? ApiKeyHint { get; init; }
    /// <summary>Where the effective key comes from: "store" | "env" | "none" —
    /// independent of <see cref="Source"/> (endpoint/model origin).</summary>
    public required string ApiKeySource { get; init; }
    /// <summary>Where endpoint/model come from: "store" | "env" | "none".</summary>
    public required string Source { get; init; }
    /// <summary>True when env/config supplies a non-none chat provider.</summary>
    public required bool EnvConfigured { get; init; }
    /// <summary>Last update of the stored override, null when Source != store.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>PUT /api/settings/chat body. Blank <see cref="ApiKey"/> keeps the stored key.</summary>
public sealed record SaveChatSettingsRequest
{
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
}

/// <summary>POST /api/settings/chat/test body — every field optional; blanks fall
/// back to the effective (store → env) configuration.</summary>
public sealed record TestChatConnectionRequest
{
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
}

/// <summary>Connection-test outcome. <see cref="ModelListed"/> is null when the
/// probe response was not a model list (or no model was supplied).</summary>
public sealed record TestChatConnectionResponse
{
    public required bool Ok { get; init; }
    public required double LatencyMs { get; init; }
    public bool? ModelListed { get; init; }
    /// <summary>Sanitized failure reason ("HTTP 401", "timeout", "connection failed").</summary>
    public string? Detail { get; init; }
}

/// <summary>GET /api/settings/{chat|assistant}/models — ids returned by
/// GET {endpoint}/v1/models, used to populate the Settings model comboboxes.
/// <see cref="Models"/> is empty on failure; <see cref="Detail"/> is a
/// sanitized reason ("HTTP 401", "timeout", "connection failed").</summary>
public sealed record ProviderModelsResponse
{
    public required bool Ok { get; init; }
    public string[] Models { get; init; } = [];
    public string? Detail { get; init; }
}

/// <summary>Per-API-key effective chat-provider state — never carries the API key itself
/// (SPEC-20260916-api-key-settings RF-003).</summary>
public sealed record ApiKeyIntegrationKeyDto(string? Hint, bool HasKey);

public sealed record ApiKeyChatSettingsDto : ChatSettingsDto
{
    /// <summary>True when this API key has its own endpoint/model override.</summary>
    public required bool HasOverride { get; init; }
    /// <summary>Which fields are overridden: endpoint, model, apiKey, or none.</summary>
    public required string[] OverrideFields { get; init; }
    /// <summary>Per-integration API key status (firecrawl, tavily, context7).</summary>
    public Dictionary<string, ApiKeyIntegrationKeyDto>? IntegrationKeys { get; init; }
}

/// <summary>PUT /api/api-keys/{id}/settings/chat body. Null fields inherit from global.
/// Blank apiKey keeps the stored key; null apiKey removes the override.</summary>
public sealed record SaveApiKeyChatSettingsRequest
{
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
}
