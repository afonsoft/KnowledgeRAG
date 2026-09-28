namespace KnowledgeHub.Server.Chat;

/// <summary>Provider-side failure (HTTP error, timeout, malformed payload) — maps to HTTP 502 / MCP isError.
/// <see cref="StatusCode"/> and <see cref="IsTimeout"/> let the resilience layer
/// (SPEC-20260927-tool-and-model-resilience-fallback) classify transient vs
/// permanent failures without parsing messages.</summary>
public sealed class ChatProviderException(string message, int? statusCode = null, bool isTimeout = false)
    : Exception(message)
{
    /// <summary>Upstream HTTP status, when the provider returned one.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>True for local/socket timeouts (no response received).</summary>
    public bool IsTimeout { get; } = isTimeout;
}
