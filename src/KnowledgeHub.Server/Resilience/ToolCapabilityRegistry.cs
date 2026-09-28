using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Resilience;

/// <summary>
/// Capability taxonomy for transparent tool substitution (RF-003): a tool may
/// only replace another in the same category — never across security/scope
/// boundaries. Default map seeds the documented pairs; configuration
/// (<c>Resilience:Fallback:ToolCapabilities</c>) extends or overrides it.
/// </summary>
public sealed class ToolCapabilityRegistry
{
    private readonly Dictionary<string, List<string>> _capabilityToTools;
    private readonly Dictionary<string, string> _toolToCapability;

    public ToolCapabilityRegistry(IOptions<FallbackOptions>? options = null)
        : this(options?.Value.ToolCapabilities) { }

    public ToolCapabilityRegistry(IReadOnlyDictionary<string, List<string>>? configured)
    {
        _capabilityToTools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["WebSearch"] = ["tavily", "firecrawl", "duckduckgo"],
            ["DeepDocLookup"] = ["deepwiki", "context7", "internal_fts"]
        };
        if (configured is not null)
            foreach (var (cap, tools) in configured)
                _capabilityToTools[cap] = tools;

        _toolToCapability = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cap, tools) in _capabilityToTools)
            foreach (var t in tools)
                _toolToCapability[t] = cap;
    }

    /// <summary>Capability category a tool belongs to; null when unmapped.</summary>
    public string? GetCapability(string toolName) =>
        _toolToCapability.TryGetValue(toolName, out var cap) ? cap : null;

    /// <summary>
    /// Ordered same-capability candidates after <paramref name="toolName"/>,
    /// filtered by <paramref name="isAvailable"/> (credentials/scope). The
    /// failed tool itself never reappears.
    /// </summary>
    public IReadOnlyList<string> CandidatesFor(
        string toolName, Func<string, bool>? isAvailable = null)
    {
        if (GetCapability(toolName) is not { } cap)
            return [];
        var tools = _capabilityToTools[cap];
        var idx = tools.FindIndex(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return [];
        return tools.Skip(idx + 1)
            .Where(t => isAvailable?.Invoke(t) ?? true)
            .ToList();
    }
}
