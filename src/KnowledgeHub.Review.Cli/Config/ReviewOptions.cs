namespace KnowledgeHub.Review.Config;

/// <summary>
/// Review CLI configuration. Every value maps to a <c>REVIEW_*</c> env var;
/// GitHub context vars (<c>GITHUB_TOKEN</c>, <c>GITHUB_REPOSITORY</c>, …) are
/// read from the Actions runtime. <see cref="FromEnvironment"/> is the single
/// source of truth — no config file parsing in v1.
/// </summary>
public sealed record ReviewOptions
{
    /// <summary>GitHub token (GITHUB_TOKEN in Actions, PAT locally).</summary>
    public string? GitHubToken { get; init; }

    /// <summary>"owner/repo" — from --repo or GITHUB_REPOSITORY.</summary>
    public string? Repository { get; init; }

    /// <summary>PR number — from --pr or GITHUB_REF_NAME.</summary>
    public int? PrNumber { get; init; }

    /// <summary>Default bot authors ingested besides the allowlist.</summary>
    public IReadOnlyList<string> BotAuthors { get; init; } = [];

    /// <summary>OpenAI-compatible chat endpoint (REVIEW_LLM_ENDPOINT).</summary>
    public string? LlmEndpoint { get; init; }

    /// <summary>Model id (REVIEW_LLM_MODEL).</summary>
    public string? LlmModel { get; init; }

    /// <summary>API key (REVIEW_LLM_API_KEY).</summary>
    public string? LlmApiKey { get; init; }

    /// <summary>Hub base URL (KNOWLEDGE_HUB_URL).</summary>
    public string? HubUrl { get; init; }

    /// <summary>Hub API key aft_* (KNOWLEDGE_HUB_API_KEY).</summary>
    public string? HubApiKey { get; init; }

    /// <summary>Reasoning backend: "local" | "hub" (REVIEW_REASONING).</summary>
    public string Reasoning { get; init; } = "local";

    /// <summary>Output language (REVIEW_LANGUAGE, default pt-BR; REVIEW.md wins).</summary>
    public string Language { get; init; } = "pt-BR";

    /// <summary>No mutations — plan printed to stdout (REVIEW_DRY_RUN).</summary>
    public bool DryRun { get; init; }

    /// <summary>Minimum LLM confidence to keep a finding (REVIEW_MIN_CONFIDENCE).</summary>
    public double MinConfidence { get; init; } = 0.6;

    /// <summary>Max diff size analyzed before summary-only mode (REVIEW_MAX_DIFF_KB).</summary>
    public int MaxDiffKb { get; init; } = 256;

    /// <summary>Signal wait timeout, minutes (REVIEW_SIGNAL_TIMEOUT_MIN).</summary>
    public int SignalTimeoutMin { get; init; } = 15;

    /// <summary>LLM passes per chunk for consensus voting (REVIEW_PASSES, 1–4).</summary>
    public int Passes { get; init; } = 1;

    /// <summary>Merge method for auto-merge (REVIEW_MERGE_METHOD).</summary>
    public string MergeMethod { get; init; } = "SQUASH";

    /// <summary>Label that skips the review (REVIEW_SKIP_LABEL).</summary>
    public string SkipLabel { get; init; } = "knowledge-review:skip";

    /// <summary>Commit status context (REVIEW_STATUS_CONTEXT).</summary>
    public string StatusContext { get; init; } = "knowledge-review/verdict";

    /// <summary>Writes a knowledge doc after the review (REVIEW_WRITE_KNOWLEDGE).</summary>
    public bool WriteKnowledge { get; init; } = true;

    public static ReviewOptions FromEnvironment() => new()
    {
        GitHubToken = Env("GITHUB_TOKEN") ?? Env("GH_TOKEN"),
        Repository = Env("GITHUB_REPOSITORY"),
        BotAuthors = Split(Env("REVIEW_BOT_AUTHORS")),
        LlmEndpoint = Env("REVIEW_LLM_ENDPOINT"),
        LlmModel = Env("REVIEW_LLM_MODEL"),
        LlmApiKey = Env("REVIEW_LLM_API_KEY") ?? Env("LLM_API_KEY"),
        HubUrl = Env("KNOWLEDGE_HUB_URL"),
        HubApiKey = Env("KNOWLEDGE_HUB_API_KEY"),
        Reasoning = Env("REVIEW_REASONING") ?? "local",
        Language = Env("REVIEW_LANGUAGE") ?? "pt-BR",
        DryRun = Bool("REVIEW_DRY_RUN"),
        MinConfidence = Double("REVIEW_MIN_CONFIDENCE", 0.6),
        MaxDiffKb = Int("REVIEW_MAX_DIFF_KB", 256),
        SignalTimeoutMin = Int("REVIEW_SIGNAL_TIMEOUT_MIN", 15),
        Passes = Math.Clamp(Int("REVIEW_PASSES", 1), 1, 4),
        MergeMethod = Env("REVIEW_MERGE_METHOD") ?? "SQUASH",
        SkipLabel = Env("REVIEW_SKIP_LABEL") ?? "knowledge-review:skip",
        StatusContext = Env("REVIEW_STATUS_CONTEXT") ?? "knowledge-review/verdict",
        WriteKnowledge = !Bool("REVIEW_NO_KNOWLEDGE"),
    };

    /// <summary>Bot logins always ingested (before the env allowlist).</summary>
    public static readonly IReadOnlyList<string> DefaultBotAuthors =
        ["devin-ai-integration", "sonarcloud", "github-advanced-security", "codeql", "coderabbitai", "qodo-merge"];

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static bool Bool(string name) =>
        Env(name) is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static int Int(string name, int fallback) =>
        int.TryParse(Env(name), out var v) ? v : fallback;

    private static double Double(string name, double fallback) =>
        double.TryParse(Env(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
