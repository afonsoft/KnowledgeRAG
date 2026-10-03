namespace KnowledgeHub.Shared.Contracts;

/// <summary>Effective embedding/indexing-provider state for the Settings UI —
/// never carries the API key itself (SPEC-20260926-settings-ux-embeddings RF-004).</summary>
public record EmbeddingSettingsDto
{
    /// <summary>Effective provider: "deterministic" | "ollama" | "openai" | "onnx".</summary>
    public required string Provider { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public required int Dimensions { get; init; }
    public string? ModelPath { get; init; }
    /// <summary>Effective chunking knobs (store value or env default).</summary>
    public required int MaxTokens { get; init; }
    public required int OverlapTokens { get; init; }
    /// <summary>True when an effective API key exists (store or env/config).</summary>
    public required bool HasApiKey { get; init; }
    public string? ApiKeyHint { get; init; }
    /// <summary>Where the effective key comes from: "store" | "env" | "none".</summary>
    public required string ApiKeySource { get; init; }
    /// <summary>Where the config comes from: "store" | "env".</summary>
    public required string Source { get; init; }
    /// <summary>Model identity stamped on stored vectors (resolver's current provider).</summary>
    public string? StampedModelId { get; init; }
    /// <summary>SPEC-20260926-embeddings-runtime-coherence RF-002: short digest
    /// when the live provider failed to build (e.g. missing ONNX model) —
    /// <see cref="StampedModelId"/> is null in that case.</summary>
    public string? ProviderError { get; init; }
    /// <summary>SPEC-20260926-embeddings-runtime-coherence RF-001: dimension the
    /// live vector store accepts — null when the store is unconstrained.</summary>
    public int? StoreDimensions { get; init; }
    /// <summary>True when effective dims differ from <see cref="StoreDimensions"/> —
    /// new vectors would be rejected until aligned + reindexed.</summary>
    public bool DimsMismatch => StoreDimensions is { } d && d != Dimensions;
    /// <summary>SPEC-20260926-embeddings-swap-safety RF-004: settings live in
    /// the instance's local SQLite — replicas do NOT converge automatically;
    /// honest flag so clients can surface "per-instance" to the operator.</summary>
    public bool InstanceScoped { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>PUT /api/settings/embeddings body. Blank <see cref="ApiKey"/> keeps the
/// stored key; blank optional fields keep env fallbacks via clear-restore.</summary>
public sealed record SaveEmbeddingSettingsRequest
{
    public required string Provider { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public int Dimensions { get; set; } = 384;
    public string? ModelPath { get; init; }
    public int? MaxTokens { get; init; }
    public int? OverlapTokens { get; init; }
    public string? ApiKey { get; init; }
}
