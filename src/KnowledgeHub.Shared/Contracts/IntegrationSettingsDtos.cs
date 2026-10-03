namespace KnowledgeHub.Shared.Contracts;

/// <summary>One upstream integration's credential state — never carries the secret
/// (SPEC-20260916-firecrawl-mcp-proxy RF-005).</summary>
public sealed record IntegrationSettingsDto
{
    /// <summary>Provider slug — "firecrawl" | "deepwiki".</summary>
    public required string Provider { get; init; }
    /// <summary>Human-facing provider name for the Settings UI.</summary>
    public required string DisplayName { get; init; }
    /// <summary>True when an effective key exists (store or env/config).</summary>
    public required bool HasKey { get; init; }
    /// <summary>Masked hint for display (e.g. "fc-••••wxyz"), null when no key.</summary>
    public string? KeyHint { get; init; }
    /// <summary>Where the effective key comes from: "store" | "env" | "none".</summary>
    public required string Source { get; init; }
    /// <summary>Optional guidance shown in the UI (e.g. what the key enables).</summary>
    public string? Note { get; init; }
    /// <summary>Runtime toggle — disabled integrations drop out of the MCP
    /// tools catalog entirely (SPEC-20260926-integration-toggle).</summary>
    public bool Enabled { get; set; } = true;
}

public sealed record IntegrationSettingsResponse
{
    public required IReadOnlyList<IntegrationSettingsDto> Integrations { get; init; }
}

/// <summary>PUT /api/settings/integrations/{provider} body.</summary>
public sealed record SetIntegrationKeyRequest
{
    public required string ApiKey { get; init; }
}

/// <summary>PUT /api/settings/integrations/{provider}/enabled body.</summary>
public sealed record SetIntegrationEnabledRequest
{
    public required bool Enabled { get; init; }
}
