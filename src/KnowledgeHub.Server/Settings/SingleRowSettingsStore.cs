using KnowledgeHub.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Shared single-row settings persistence (SPEC-20260928-resilience-tool-
/// fallback-wiring): owns the scope/lock/read/upsert/clear mechanics that
/// single-row settings services (Graph, Resilience) used to duplicate.
/// <typeparamref name="T"/> must have an <c>Id</c> int PK — the store always
/// upserts row <c>Id = 1</c>.
/// </summary>
public abstract class SingleRowSettingsStore<T>(IServiceScopeFactory scopeFactory)
    where T : class, ISingleRowSettings, new()
{
    /// <summary>Scope factory — exposed for derived stores that need their
    /// own scope (e.g. compare-and-swap write-backs outside UpsertAsync).</summary>
    protected IServiceScopeFactory ScopeFactory => scopeFactory;

    /// <summary>DbSet accessor — implemented per concrete store.</summary>
    protected abstract DbSet<T> Set(KnowledgeHubDbContext db);

    /// <summary>Reads the single row (as-no-tracking, own scope).</summary>
    protected async Task<T?> FindRowAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        return await Set(db).AsNoTracking().SingleOrDefaultAsync(ct);
    }

    /// <summary>Runs <paramref name="mutate"/> inside an upsert scope —
    /// creates row Id=1 when absent, then saves.</summary>
    protected async Task UpsertAsync(
        Func<T, Task> mutate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var row = await Set(db).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            row = Activator.CreateInstance<T>();
            row.Id = 1;
            Set(db).Add(row);
        }
        await mutate(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the single row if present.</summary>
    protected async Task DeleteRowAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        await Set(db).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Marker for single-row settings entities (Id PK).</summary>
public interface ISingleRowSettings
{
    int Id { get; set; }
}

/// <summary>
/// Lazily-loaded volatile snapshot with invalidation — the lock + double-check
/// that single-row settings services share. Async load happens under the gate
/// (the pattern Graph/Resilience settings use — callers are cheap paths).
/// </summary>
public sealed class SnapshotCache<T> where T : class
{
    private readonly object _gate = new();
    private T? _snapshot;

    /// <summary>Returns the cached snapshot, loading via <paramref name="load"/>
    /// on first access or after <see cref="Invalidate"/>.</summary>
    public T Get(Func<Task<T>> load)
    {
        if (_snapshot is { } hit)
            return hit;
        lock (_gate)
            return _snapshot ??= load().GetAwaiter().GetResult();
    }

    /// <summary>Drops the snapshot — next <see cref="Get"/> reloads.</summary>
    public void Invalidate()
    {
        lock (_gate)
            _snapshot = null;
    }
}
