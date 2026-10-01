using KnowledgeHub.McpEngine.Activity;

namespace KnowledgeHub.Tests.Unit.McpEngine;

// Covers RF-004: per-session concurrency gate
public class SessionCallGateTests
{
    [Fact]
    public async Task AcquireAsync_BlocksBeyondLimit_PerSession()
    {
        var gate = new SessionCallGate(maxConcurrentPerSession: 2);
        await gate.AcquireAsync("s1", CancellationToken.None);
        await gate.AcquireAsync("s1", CancellationToken.None);

        var third = gate.AcquireAsync("s1", CancellationToken.None);
        Assert.False(third.IsCompleted);

        // a different session is unaffected
        await gate.AcquireAsync("s2", CancellationToken.None);
        gate.Release("s2");

        gate.Release("s1");
        await third; // now unblocked
        gate.Release("s1");
        gate.Release("s1");
    }

    [Fact]
    public async Task Release_WithoutAcquire_IsNoOp()
    {
        var gate = new SessionCallGate(1);
        gate.Release("never-acquired"); // must not throw
        var acquire = gate.AcquireAsync("never-acquired", CancellationToken.None);
        Assert.True(acquire.IsCompleted); // gate remains usable after the stray release
    }

    // Covers SPEC-20260918-mcp-v2-hybrid-transport RF-004: sessionless calls
    // (hybrid transport serves 2026-07-28 clients without a session id) must be
    // bounded by a single shared bucket instead of bypassing the gate.
    [Fact]
    public async Task AcquireAsync_Sessionless_SharesBoundedBucket()
    {
        var gate = new SessionCallGate(maxConcurrentPerSession: 2);
        await gate.AcquireAsync(null, CancellationToken.None);
        await gate.AcquireAsync(null, CancellationToken.None);

        var third = gate.AcquireAsync(null, CancellationToken.None);
        Assert.False(third.IsCompleted); // shared bucket is full → queued, not unbounded

        gate.Release(null);
        await third;
        gate.Release(null);
        gate.Release(null);
    }

    [Fact]
    public async Task AcquireAsync_Sessionless_DoesNotContendWithSessions()
    {
        // The shared sessionless bucket must not collide with real session ids.
        var gate = new SessionCallGate(maxConcurrentPerSession: 1);
        await gate.AcquireAsync(null, CancellationToken.None); // fills sessionless bucket

        var s1 = gate.AcquireAsync("s1", CancellationToken.None);
        Assert.True(s1.IsCompleted); // different bucket → immediate
        gate.Release("s1");
        gate.Release(null);
    }

    [Fact]
    public async Task Release_Sessionless_WithoutAcquire_IsNoOp()
    {
        var gate = new SessionCallGate(1);
        gate.Release(null); // must not over-release the shared bucket
        var acquire = gate.AcquireAsync(null, CancellationToken.None);
        Assert.True(acquire.IsCompleted);
    }
}
