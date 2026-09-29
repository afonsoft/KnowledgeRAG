namespace KnowledgeHub.Server.Resilience;

/// <summary>Outcome of one policy evaluation.</summary>
public sealed record FallbackDecision(
    /// <summary>Enforce + eligible failure + budget left → try the candidate.</summary>
    bool ShouldFallback,
    /// <summary>Machine reason tag for logs/metadata.</summary>
    string Reason,
    /// <summary>Mode that produced the decision.</summary>
    FallbackMode Mode);

/// <summary>
/// Central policy point (RF-001/RF-004): classifies a failure under the
/// configured mode and attempt budget. Stateless aside from options.
/// </summary>
public interface IFallbackPolicyEngine
{
    FallbackMode Mode { get; }
    int MaxAttempts { get; }

    /// <summary>Decides whether <paramref name="failure"/> on <paramref name="category"/>
    /// ("chat"/"embeddings"/"tools") should trigger the next candidate.</summary>
    FallbackDecision Evaluate(
        Exception failure, string category, int attempt, CancellationToken ct);

    /// <summary>
    /// Same policy check for failures that surface as a pre-classified reason
    /// tag instead of an exception — tool calls that return
    /// <c>IsError=true</c> results (SPEC-20260928-resilience-tool-fallback-wiring).
    /// Callers must only pass transient-classified reasons; permanent/unknown
    /// tool errors never reach this method.
    /// </summary>
    FallbackDecision EvaluateReason(
        string reason, string category, int attempt, CancellationToken ct);
}
