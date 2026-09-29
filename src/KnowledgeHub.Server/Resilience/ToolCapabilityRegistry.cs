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

    /// <summary>Preferred tool name per provider entry — the concrete catalog
    /// tool a capability maps to (SPEC-20260928 RF-001). Providers without a
    /// canonical tool fall back to a <c>{provider}_*</c> prefix match.</summary>
    private static readonly IReadOnlyDictionary<string, string> PreferredToolByProvider =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tavily"] = "tavily_search",
            ["firecrawl"] = "firecrawl_search",
            ["duckduckgo"] = "duckduckgo_search",
            ["deepwiki"] = "ask_question",
            ["context7"] = "query-docs",
            ["internal_fts"] = "search_knowledge"
        };

    /// <summary>Maps a catalog tool name back to its provider entry —
    /// exact match, preferred-tool match, or <c>{provider}_*</c> prefix.</summary>
    public string? ProviderForTool(string toolName)
    {
        if (_toolToCapability.ContainsKey(toolName))
            return toolName;
        foreach (var (provider, preferred) in PreferredToolByProvider)
            if (toolName.Equals(preferred, StringComparison.OrdinalIgnoreCase)
                && _toolToCapability.ContainsKey(provider))
                return provider;
        // Longest-prefix wins so "deepwiki_private_x" maps to deepwiki_private, not deepwiki.
        return _toolToCapability.Keys
            .Where(p => toolName.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
    }

    /// <summary>Capability of a *catalog tool name* (resolved via provider).</summary>
    public string? GetCapabilityForTool(string toolName) =>
        ProviderForTool(toolName) is { } p ? _toolToCapability[p] : null;

    /// <summary>
    /// Ordered candidate *tool names* for the capability of
    /// <paramref name="toolName"/>, resolved against the caller-visible
    /// <paramref name="availableToolNames"/> (scope-filtered catalog). The
    /// failed tool's provider never reappears; providers without a live tool
    /// are skipped.
    /// </summary>
    public IReadOnlyList<string> CandidateToolNames(
        string toolName, IReadOnlyCollection<string> availableToolNames)
    {
        if (ProviderForTool(toolName) is not { } provider
            || GetCapability(provider) is not { } cap)
            return [];

        var providers = _capabilityToTools[cap];
        var idx = providers.FindIndex(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return [];

        return providers.Skip(idx + 1)
            .Select(candidate => ResolveToolName(candidate, availableToolNames))
            .OfType<string>()
            .ToList();
    }

    /// <summary>Provider → concrete tool name: preferred map, exact name,
    /// then first <c>{provider}_*</c> catalog match.</summary>
    private static string? ResolveToolName(string provider, IReadOnlyCollection<string> available)
    {
        if (PreferredToolByProvider.TryGetValue(provider, out var preferred)
            && available.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            return preferred;
        if (available.Contains(provider, StringComparer.OrdinalIgnoreCase))
            return provider;
        return available.FirstOrDefault(n =>
            n.StartsWith(provider + "_", StringComparison.OrdinalIgnoreCase));
    }

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
