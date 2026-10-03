namespace KnowledgeHub.Server.Mcp.Upstream;

/// <summary>
/// TTL cache for upstream <c>tools/list</c> results, shared by the upstream
/// tool providers (tavily, firecrawl, deepwiki, context7). The tools list and
/// its expiry travel in ONE reference — reads and invalidation are a single
/// atomic swap with no lock on either path; fetches are single-flight under a
/// gate; on failure the last-known-good set (even expired) is served.
/// </summary>
internal sealed class UpstreamToolsCache(
    Func<CancellationToken, Task<IReadOnlyList<CatalogTool>>> fetch,
    TimeSpan ttl)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;
    private sealed record Snapshot(IReadOnlyList<CatalogTool> Tools, DateTimeOffset ExpiresAt);

    /// <summary>Fresh tools when cached, otherwise a single-flight fetch.
    /// Fetch failures go to <paramref name="onFetchError"/> and the
    /// last-known-good set is served (possibly expired) — empty when nothing
    /// was ever fetched.</summary>
    public async Task<IReadOnlyList<CatalogTool>> GetAsync(
        Action<Exception> onFetchError, CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && DateTimeOffset.UtcNow < snapshot.ExpiresAt)
            return snapshot.Tools;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && DateTimeOffset.UtcNow < snapshot.ExpiresAt)
                return snapshot.Tools;
            try
            {
                _snapshot = new Snapshot(await fetch(cancellationToken), DateTimeOffset.UtcNow + ttl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // last-known-good wins; empty when nothing was ever fetched.
                onFetchError(ex);
            }
            return _snapshot?.Tools ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lock-free: the next reader sees a null snapshot and re-fetches —
    /// no semaphore Wait() blocking a thread-pool thread on the settings path.</summary>
    public void Invalidate() => Interlocked.Exchange(ref _snapshot, null);
}
