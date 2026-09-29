using KnowledgeHub.Server.Settings;
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
/// Options resolve per call through <see cref="IResilienceSettingsService"/> so
/// /settings edits apply without restart (SPEC-20260928 RF-004).
/// </summary>
public sealed class FallbackPolicyEngine(
    IOptions<FallbackOptions> options,
    ILogger<FallbackPolicyEngine> logger,
    IResilienceSettingsService? settings = null) : IFallbackPolicyEngine
{
    private readonly FallbackOptions _staticOptions = options.Value;

    public FallbackMode Mode => Effective().ParseMode();
    public int MaxAttempts => Math.Max(0, Effective().MaxFallbackAttempts);

    /// <summary>Persisted override → configured options. The service is
    /// optional so unit tests can inject bare options.</summary>
    public FallbackOptions Effective() => settings?.GetEffective() ?? _staticOptions;

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

        return WithinBudget(reason, category, attempt);
    }

    /// <inheritdoc/>
    public FallbackDecision EvaluateReason(
        string reason, string category, int attempt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new FallbackDecision(false, "CancelledByCaller", Mode);

        if (Mode is FallbackMode.Disabled)
            return new FallbackDecision(false, reason, Mode);

        return WithinBudget(reason, category, attempt);
    }

    private FallbackDecision WithinBudget(string reason, string category, int attempt)
    {
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
