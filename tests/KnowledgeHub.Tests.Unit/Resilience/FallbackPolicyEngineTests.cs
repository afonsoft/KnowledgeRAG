using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Tests.Unit.Resilience;

// SPEC-20260927-tool-and-model-resilience-fallback RF-001/RF-002/RF-004:
// mode transitions, error classification and attempt budget.
public sealed class FallbackPolicyEngineTests
{
    private static FallbackPolicyEngine Engine(string mode, int max = 2) =>
        new(Options.Create(new FallbackOptions { Mode = mode, MaxFallbackAttempts = max }),
            NullLogger<FallbackPolicyEngine>.Instance);

    private static Exception Transient() => new ChatProviderException("rate limited", 429);
    private static Exception Permanent() => new ChatProviderException("bad key", 401);

    [Fact]
    public void Disabled_NeverFallsBack()
    {
        var d = Engine("disabled").Evaluate(Transient(), "chat", 0, CancellationToken.None);
        Assert.False(d.ShouldFallback);
        Assert.Equal(FallbackMode.Disabled, d.Mode);
    }

    // AC-2: Observe classifies + logs but rethrows the original failure.
    [Fact]
    public void Observe_DoesNotFallback()
    {
        var d = Engine("observe").Evaluate(Transient(), "chat", 0, CancellationToken.None);
        Assert.False(d.ShouldFallback);
        Assert.Equal("HttpError_429", d.Reason);
        Assert.Equal(FallbackMode.Observe, d.Mode);
    }

    // AC-1: Enforce + transient 429 → fallback candidate allowed.
    [Fact]
    public void Enforce_AllowsFallbackOn429()
    {
        var d = Engine("enforce").Evaluate(Transient(), "chat", 0, CancellationToken.None);
        Assert.True(d.ShouldFallback);
        Assert.Equal("HttpError_429", d.Reason);
    }

    // AC-3: 401 is a permanent auth fault — never falls back even in Enforce.
    [Fact]
    public void Enforce_NeverFallsBackOn401()
    {
        var d = Engine("enforce").Evaluate(Permanent(), "chat", 0, CancellationToken.None);
        Assert.False(d.ShouldFallback);
        Assert.Equal("HttpError_401", d.Reason);
    }

    // Timeouts are transient.
    [Fact]
    public void Timeout_IsTransient()
    {
        var d = Engine("enforce").Evaluate(
            new ChatProviderException("timed out", isTimeout: true), "chat", 0, CancellationToken.None);
        Assert.True(d.ShouldFallback);
        Assert.Equal("Timeout", d.Reason);
    }

    // RF-004: budget cap — attempt >= MaxAttempts refuses more alternates.
    [Fact]
    public void BudgetExhausted_RefusesMore()
    {
        var d = Engine("enforce", max: 2).Evaluate(Transient(), "chat", 2, CancellationToken.None);
        Assert.False(d.ShouldFallback);
        Assert.Equal("BudgetExhausted", d.Reason);
    }

    // AC-4: caller cancellation always wins — no further providers.
    [Fact]
    public void Cancellation_AbortsFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var d = Engine("enforce").Evaluate(Transient(), "chat", 0, cts.Token);
        Assert.False(d.ShouldFallback);
        Assert.Equal("CancelledByCaller", d.Reason);
    }

    // Non-transient arbitrary exceptions don't trigger fallback.
    [Fact]
    public void UnknownException_NoFallback()
    {
        var d = Engine("enforce").Evaluate(new InvalidDataException("x"), "chat", 0, CancellationToken.None);
        Assert.False(d.ShouldFallback);
    }
}
