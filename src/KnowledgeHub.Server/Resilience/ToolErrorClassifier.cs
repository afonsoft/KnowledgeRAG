namespace KnowledgeHub.Server.Resilience;

/// <summary>
/// Classification of tool-call failures that surface as
/// <c>CallToolResult.IsError=true</c> instead of exceptions — upstream MCP
/// clients normalize failures to error results ("tavily upstream error: …"),
/// so the exception-based <see cref="FallbackErrorClassifier"/> never sees them
/// (SPEC-20260928-resilience-tool-fallback-wiring RF-001).
/// </summary>
public static class ToolErrorClassifier
{
    /// <summary>Why a result can't drive a fallback decision.</summary>
    public enum ToolErrorClass
    {
        /// <summary>Transient upstream fault — eligible for fallback.</summary>
        Transient,
        /// <summary>Client/auth/validation fault — never fallback.</summary>
        Permanent,
        /// <summary>Unrecognized error — treated as permanent (conservative).</summary>
        Unknown
    }

    private static readonly string[] TransientMarkers =
    [
        "timeout", "timed out", "429", "rate limit", "too many",
        "502", "503", "504", "unavailable", "upstream error",
        "connection", "reset", "temporarily"
    ];

    private static readonly string[] PermanentMarkers =
    [
        "401", "403", "404", "unauthorized", "forbidden",
        "invalid api", "invalid key", "authentication", "not found",
        "bad request", "400"
    ];

    /// <summary>
    /// Classifies the text content of an error result. Permanent markers win
    /// over transient ones when both appear.
    /// </summary>
    public static ToolErrorClass Classify(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return ToolErrorClass.Unknown;
        var text = errorText.ToLowerInvariant();
        if (PermanentMarkers.Any(text.Contains))
            return ToolErrorClass.Permanent;
        return TransientMarkers.Any(text.Contains)
            ? ToolErrorClass.Transient
            : ToolErrorClass.Unknown;
    }

    /// <summary>Stable reason tag for an error result — mirrors
    /// <see cref="FallbackErrorClassifier.ReasonFor"/> conventions.</summary>
    public static string ReasonFor(string? errorText)
    {
        var text = errorText?.ToLowerInvariant() ?? "";
        if (text.Contains("timeout") || text.Contains("timed out"))
            return "Timeout";
        if (text.Contains("429") || text.Contains("rate limit") || text.Contains("too many"))
            return "RateLimited";
        foreach (var code in new[] { "502", "503", "504" })
            if (text.Contains(code))
                return $"HttpError_{code}";
        if (text.Contains("unavailable") || text.Contains("upstream error"))
            return "UpstreamError";
        if (text.Contains("connection") || text.Contains("reset"))
            return "NetworkError";
        return "ToolError";
    }
}
