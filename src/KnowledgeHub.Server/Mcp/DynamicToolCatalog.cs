namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// Catalog aggregator — last-registered provider wins on name collisions.
/// The aggregate is cached per <see cref="IToolCatalogChangeNotifier.Version"/>
/// (SPEC-20260916-performance-memory-cache RF-001): sources, secrets and
/// upstream sessions all invalidate through the notifier, so every consumer
/// (tools/list, tools/call, agent loop, Playground) shares one build instead of
/// re-running every provider per request. Cached <see cref="CatalogTool"/>s are
/// safe to share — handlers resolve scoped services from the call-time
/// <see cref="ToolCallContext.Services"/>.
/// </summary>
public sealed class DynamicToolCatalog(
    IEnumerable<IToolProvider> providers,
    IToolCatalogChangeNotifier notifier) : IDynamicToolCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CatalogTool>? _cached;
    private long _cachedVersion = -1;
    // SPEC-20260928-resilience-tool-fallback-wiring RF-001: the resilience
    // wrapper is applied to the caller-visible set. For unrestricted callers
    // the wrapped aggregate is cached alongside the raw one so repeated
    // GetToolsAsync calls return the same instance (catalog cache contract).
    private IReadOnlyList<CatalogTool>? _cachedWrapped;
    private long _cachedWrappedVersion = -1;

    /// <summary>SPEC-20260923-source-authorization RF-004: per-caller filtering
    /// runs on the shared cached aggregate — no per-key cache invalidation.</summary>
    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var all = await GetUnfilteredToolsAsync(services, cancellationToken);
        var scope = services.GetService<Auth.ICallerScopeProvider>() is { } provider
            ? await provider.GetAsync(cancellationToken)
            : Auth.CallerScope.Unrestricted;
        if (scope.IsUnrestricted)
        {
            var version = notifier.Version;
            if (_cachedWrapped is { } wrapped && _cachedWrappedVersion == version)
                return wrapped;
            wrapped = Resilience.ResilientToolInvoker.Wrap(all);
            _cachedWrapped = wrapped;
            _cachedWrappedVersion = version;
            return wrapped;
        }

        // Resilient handlers resolve alternates inside this caller-visible
        // set — the scope filter runs first so fallback never crosses
        // CallerScope (RF-002).
        var filtered = all
            .Where(t => scope.AllowsTool(t.Name)
                && (t.SourceId is null || scope.AllowsSource(t.SourceId.Value)))
            .ToList();
        return Resilience.ResilientToolInvoker.Wrap(filtered);
    }

    public async Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var version = notifier.Version;
        if (_cached is { } hit && _cachedVersion == version)
            return hit;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            version = notifier.Version; // re-read under the gate
            if (_cached is { } again && _cachedVersion == version)
                return again;

            var tools = new Dictionary<string, CatalogTool>(StringComparer.Ordinal);
            foreach (var provider in providers)
                foreach (var tool in await provider.GetToolsAsync(services, cancellationToken))
                    tools[tool.Name] = tool;

            _cached = [.. tools.Values];
            _cachedVersion = version;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
