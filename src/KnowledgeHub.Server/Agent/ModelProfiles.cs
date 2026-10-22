using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Agent;

/// <summary>
/// SPEC-20261021-model-capability-profiles RF-2/RF-6: builtin capability
/// catalog + pattern matching + user-override merge. Static — no DB needed
/// to resolve a model to its effective capabilities.
/// </summary>
public static class ModelProfiles
{
    /// <summary>Seeded catalog (RF-2). Ordered for readability only —
    /// resolution sorts by specificity.</summary>
    public static readonly IReadOnlyList<ModelProfile> Builtin =
    [
        P("gpt-5*", 400_000, tools: true, structured: true, reasoning: true),
        P("o1*", 200_000, tools: true, structured: true, reasoning: true),
        P("o3*", 200_000, tools: true, structured: true, reasoning: true),
        P("gpt-4o*", 128_000, tools: true, structured: true),
        P("gpt-4*", 128_000, tools: true, structured: true),
        P("claude-*", 200_000, tools: true, structured: true),
        P("llama3*", 128_000, tools: true, structured: false),
        P("mistral*", 128_000, tools: true, structured: false),
        P("qwen*", 128_000, tools: true, structured: false),
        P("deepseek-r1*", 128_000, tools: false, structured: false, reasoning: true),
        P("nomic-embed*", 8_192, tools: false, structured: false),
        P("all-minilm*", 512, tools: false, structured: false),
        P("*", 32_000, tools: true, structured: false),
    ];

    private static ModelProfile P(
        string pattern, int contextWindow,
        bool tools, bool structured, bool reasoning = false) => new()
        {
            ModelPattern = pattern,
            ContextWindowTokens = contextWindow,
            SupportsToolCalls = tools,
            SupportsStructuredOutput = structured,
            SupportsReasoning = reasoning,
            Source = "builtin"
        };

    /// <summary>Matches a pattern against a model name: "name" exact or
    /// "prefix*" wildcard. Comparison is case-insensitive.</summary>
    public static bool Matches(string pattern, string model)
    {
        if (pattern.EndsWith('*'))
            return model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best match wins: exact name > longest prefix > catch-all.</summary>
    public static ModelProfile? Resolve(string model, IEnumerable<ModelProfile> profiles)
    {
        var best = profiles
            .Where(p => Matches(p.ModelPattern, model))
            .OrderByDescending(p => p.ModelPattern.TrimEnd('*').Length)
            .FirstOrDefault();
        return best;
    }

    /// <summary>RF-2: user overrides replace builtin entries on the same
    /// pattern (case-insensitive); everything else merges.</summary>
    public static IReadOnlyList<ModelProfile> Merge(
        IEnumerable<ModelProfile> userOverrides)
    {
        var merged = Builtin.ToDictionary(
            p => p.ModelPattern, StringComparer.OrdinalIgnoreCase);
        foreach (var p in userOverrides)
            merged[p.ModelPattern] = p;
        return merged.Values.ToList();
    }

    /// <summary>RF-4: profile-derived context budget — never exceeds the
    /// configured cap; 0-context profiles keep the configured default.</summary>
    public static int ContextBudgetBytes(int configuredBytes, ModelProfile? profile)
    {
        if (profile?.ContextWindowTokens is not > 0)
            return configuredBytes;
        // ~4 bytes/token — the same approximation ApplyTokenBudget uses.
        return Math.Min(configuredBytes, profile.ContextWindowTokens * 4);
    }
}
