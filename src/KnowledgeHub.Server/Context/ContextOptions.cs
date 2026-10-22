namespace KnowledgeHub.Server.Context;

/// <summary><c>Context:*</c> — context-snapshots feature knobs
/// (SPEC-20261004-context-snapshots RF-010).</summary>
public sealed class ContextOptions
{
    public const string SectionName = "Context";

    /// <summary>Master switch for the ctx:* HybridCache layer.</summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>Retention: max snapshots kept per repo (oldest pruned on save).</summary>
    public int MaxSnapshotsPerRepo { get; set; } = 50;

    /// <summary>Max items accepted per snapshot payload.</summary>
    public int MaxItemsPerSnapshot { get; set; } = 200;

    /// <summary>Max UTF-8 bytes of the serialized payload (default 512 KB).</summary>
    public int MaxPayloadBytes { get; set; } = 512 * 1024;

    /// <summary>Default token budget when the caller omits <c>tokenBudget</c>.</summary>
    public int DefaultTokenBudget { get; set; } = 4096;

    /// <summary>Trailing items never elided (keep-tail, item semantics —
    /// distinct from <c>Agent:ContextManagement:KeepMinLastSections</c> which
    /// counts AST sections).</summary>
    public int KeepMinLastItems { get; set; } = 3;

    /// <summary>Items below this token estimate are never elided.</summary>
    public int ElisionTokenLimit { get; set; } = 150;

    /// <summary>Chars of eligible items fed to the summarizer.</summary>
    public int SummaryInputMaxChars { get; set; } = 24_000;

    /// <summary>Max tokens produced by the summarizer.</summary>
    public int SummaryMaxTokens { get; set; } = 512;

    /// <summary>Summarizer call timeout — own CTS, never inherits the request.</summary>
    public int SummaryTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout for the context_recovery wiki enrichment call
    /// (SPEC-20261022 RF-2) — bounded tighter than DeepWiki:TimeoutSeconds
    /// (60s) so a slow upstream can't stall snapshot recovery.</summary>
    public int WikiTimeoutSeconds { get; set; } = 15;
}
