using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Resilience;

/// <summary>Default policy engine (RF-001/RF-004):
/// <list type="bullet">
///   <item>Disabled → never falls back.</item>
///   <item>Observe → classifies + logs the would-be transition at Warning,
///   preserves the original failure.</item>
///   <item>Enforce → falls back while the failure is transient and the
///   attempt counter stays under <see cref="FallbackOptions.MaxFallbackAttempts"/>;
///   caller cancellation always wins (RF-004/AC-4).</item>
/// </list>
/// </summary>
public sealed class FallbackPolicyEngine(
    IOptions<FallbackOptions> options,
    ILogger<FallbackPolicyEngine> logger) : IFallbackPolicyEngine
{
    public FallbackMode Mode { get; } = options.Value.ParseMode();
    public int MaxAttempts { get; } = Math.Max(0, options.Value.MaxFallbackAttempts);

    public FallbackDecision Evaluate(
        Exception failure, string category, int attempt, CancellationToken ct)
    {
        var reason = FallbackErrorClassifier.ReasonFor(failure);

        if (ct.IsCancellationRequested || failure is OperationCanceledException)
            return new FallbackDecision(false, "CancelledByCaller", Mode);

        if (Mode is FallbackMode.Disabled)
            return new FallbackDecision(false, reason, Mode);

        if (FallbackErrorClassifier.IsPermanent(failure))
            return new FallbackDecision(false, reason, Mode);

        if (!FallbackErrorClassifier.IsTransient(failure))
            return new FallbackDecision(false, reason, Mode);

        if (Mode is FallbackMode.Observe)
        {
            logger.LogWarning(
                "fallback observe: {Category} would switch candidate after {Reason} (attempt {Attempt})",
                category, reason, attempt);
            return new FallbackDecision(false, reason, Mode);
        }

        if (attempt >= MaxAttempts)
        {
            logger.LogWarning(
                "fallback budget exhausted for {Category} after {Reason} (attempt {Attempt}/{Max})",
                category, reason, attempt, MaxAttempts);
            return new FallbackDecision(false, "BudgetExhausted", Mode);
        }

        return new FallbackDecision(true, reason, Mode);
    }
}
