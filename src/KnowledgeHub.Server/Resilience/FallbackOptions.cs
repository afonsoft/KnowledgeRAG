using KnowledgeHub.Server.Chat;

namespace KnowledgeHub.Server.Resilience;

/// <summary>Policy mode (SPEC-20260927-tool-and-model-resilience-fallback RF-001).</summary>
public enum FallbackMode
{
    /// <summary>Failures propagate immediately — no alternates attempted.</summary>
    Disabled,
    /// <summary>Fail closed: classify + log the would-be candidate, rethrow.</summary>
    Observe,
    /// <summary>Execute the transition within <see cref="FallbackOptions.MaxFallbackAttempts"/>.</summary>
    Enforce
}

/// <summary>
/// <c>Resilience:Fallback</c> configuration. Chat fallbacks carry full provider
/// options (endpoint/model/apiKey) so any OpenAI-compatible or Ollama endpoint
/// can serve as an alternate; tool capabilities map a category to an ordered
/// candidate list.
/// </summary>
public sealed class FallbackOptions
{
    public const string SectionName = "Resilience:Fallback";

    /// <summary>disabled | observe | enforce (default disabled — opt-in).</summary>
    public string Mode { get; set; } = "disabled";

    /// <summary>RF-004: maximum alternate providers tried per request (default 2).</summary>
    public int MaxFallbackAttempts { get; set; } = 2;

    /// <summary>Ordered alternate chat providers (same shape as Chat options).</summary>
    public List<ChatProviderOptions> ChatFallbacks { get; set; } = [];

    /// <summary>Capability → ordered candidate tool names (RF-003).</summary>
    public Dictionary<string, List<string>> ToolCapabilities { get; set; } = new();

    public FallbackMode ParseMode() => Mode?.Trim().ToLowerInvariant() switch
    {
        "observe" => FallbackMode.Observe,
        "enforce" => FallbackMode.Enforce,
        _ => FallbackMode.Disabled
    };
}
