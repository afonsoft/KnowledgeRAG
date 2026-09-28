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
}
