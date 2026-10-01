using System.Text.Json;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// Unit tests for <see cref="ToolCacheService"/> — SPEC-20260924-redis-cache-and-tool-caching RF-002.
/// </summary>
public sealed class ToolCacheServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IDistributedCache MakeMemoryCache() =>
        new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

    private static ToolCacheService MakeService(
        IDistributedCache cache,
        bool toolCacheEnabled = true,
        int toolCacheTtlMinutes = 60) =>
        new(cache,
            Options.Create(new CacheOptions
            {
                ToolCacheEnabled = toolCacheEnabled,
                ToolCacheTtlMinutes = toolCacheTtlMinutes
            }),
            NullLogger<ToolCacheService>.Instance);

    private static CallToolResult OkResult(string text = "result") =>
        new()
        {
            IsError = false,
            Content = [new TextContentBlock { Text = text }]
        };

    private static CallToolResult ErrorResult() =>
        new() { IsError = true, Content = [new TextContentBlock { Text = "boom" }] };

    private static IDictionary<string, JsonElement> Args(params (string k, string v)[] pairs)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in pairs)
            dict[k] = JsonDocument.Parse($"\"{v}\"").RootElement;
        return dict;
    }

    // -------------------------------------------------------------------------
    // IsCacheable
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("knowledge_search", false, true)]
    [InlineData("knowledge_ask", false, true)]
    [InlineData("read_file", false, true)]
    [InlineData("find_docs", false, true)]
    [InlineData("context7_query", false, true)]
    [InlineData("deepwiki_ask", false, true)]
    [InlineData("write_file", true, true)]   // isReadOnly=true → always cacheable
    [InlineData("write_file", false, false)] // write, not idempotent
    [InlineData("delete_chunk", false, false)]
    public void IsCacheable_ReturnsExpected_BasedOnNameAndReadOnlyFlag(
        string toolName, bool isReadOnly, bool expected)
    {
        // Given
        var svc = MakeService(MakeMemoryCache());

        // When
        var result = svc.IsCacheable(toolName, isReadOnly);

        // Then
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsCacheable_WhenToolCacheDisabled_ReturnsFalseForAll()
    {
        // Given: global flag off
        var svc = MakeService(MakeMemoryCache(), toolCacheEnabled: false);

        // When / Then
        Assert.False(svc.IsCacheable("knowledge_search", isReadOnly: false));
        Assert.False(svc.IsCacheable("read_file", isReadOnly: true));
    }

    // -------------------------------------------------------------------------
    // Cache miss → null
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCachedResultAsync_WhenCacheMiss_ReturnsNull()
    {
        // Given: empty cache
        var svc = MakeService(MakeMemoryCache());

        // When
        var result = await svc.GetCachedResultAsync("knowledge_search", Args(("q", "hello")));

        // Then
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Round-trip: set → get
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetThenGet_ReturnsDeserializedResult()
    {
        // Given
        var cache = MakeMemoryCache();
        var svc = MakeService(cache);
        var arguments = Args(("query", "dotnet"));

        // When
        await svc.SetCachedResultAsync("knowledge_search", arguments, OkResult("answer42"));
        var cached = await svc.GetCachedResultAsync("knowledge_search", arguments);

        // Then
        Assert.NotNull(cached);
        var text = Assert.IsType<TextContentBlock>(cached!.Content![0]);
        Assert.Equal("answer42", text.Text);
        Assert.False(cached.IsError);
    }

    // -------------------------------------------------------------------------
    // Error results must NOT be cached
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetCachedResultAsync_WhenResultIsError_DoesNotCache()
    {
        // Given
        var cache = MakeMemoryCache();
        var svc = MakeService(cache);
        var arguments = Args(("q", "fail"));

        // When
        await svc.SetCachedResultAsync("knowledge_search", arguments, ErrorResult());
        var cached = await svc.GetCachedResultAsync("knowledge_search", arguments);

        // Then: error results must never be stored
        Assert.Null(cached);
    }

    // -------------------------------------------------------------------------
    // Argument order should NOT affect the cache key (canonical ordering)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCachedResultAsync_ArgOrderIndependent_HitsCache()
    {
        // Given: set with args in one order
        var cache = MakeMemoryCache();
        var svc = MakeService(cache);

        var argsAbc = Args(("a", "1"), ("b", "2"), ("c", "3"));
        var argsCba = Args(("c", "3"), ("b", "2"), ("a", "1")); // reversed order

        await svc.SetCachedResultAsync("read_file", argsAbc, OkResult("file-content"));

        // When: get with reversed arg order
        var result = await svc.GetCachedResultAsync("read_file", argsCba);

        // Then: same canonical key → cache hit
        Assert.NotNull(result);
        var text = Assert.IsType<TextContentBlock>(result!.Content![0]);
        Assert.Equal("file-content", text.Text);
    }

    // -------------------------------------------------------------------------
    // Different tool names → different keys
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCachedResultAsync_DifferentToolName_ReturnsNull()
    {
        // Given
        var cache = MakeMemoryCache();
        var svc = MakeService(cache);
        var arguments = Args(("q", "test"));

        await svc.SetCachedResultAsync("knowledge_search", arguments, OkResult("r1"));

        // When
        var result = await svc.GetCachedResultAsync("knowledge_ask", arguments);

        // Then: different tool name → different key → miss
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // TTL is enforced at minimum 60 minutes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetCachedResultAsync_EnforcesMinimumTtlOf60Minutes()
    {
        // Given: configured with 5 minutes (below minimum)
        var trackingCache = new TrackingCache();
        var svc = MakeService(trackingCache, toolCacheTtlMinutes: 5);

        // When
        await svc.SetCachedResultAsync("read_file", null, OkResult());

        // Then: TTL must be clamped to at least 60 minutes
        Assert.True(trackingCache.LastTtl >= TimeSpan.FromMinutes(60),
            $"Expected TTL >= 60 minutes, got {trackingCache.LastTtl}");
    }

    [Fact]
    public async Task SetCachedResultAsync_WhenTtlAboveMinimum_UsesConfiguredValue()
    {
        // Given: 120 minutes configured
        var trackingCache = new TrackingCache();
        var svc = MakeService(trackingCache, toolCacheTtlMinutes: 120);

        // When
        await svc.SetCachedResultAsync("read_file", null, OkResult());

        // Then: TTL should be 120 minutes (or close)
        Assert.True(trackingCache.LastTtl >= TimeSpan.FromMinutes(119),
            $"Expected ~120 min TTL, got {trackingCache.LastTtl}");
    }

    // -------------------------------------------------------------------------
    // Null / empty arguments
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_WithNullArguments_Works()
    {
        // Given
        var cache = MakeMemoryCache();
        var svc = MakeService(cache);

        // When
        await svc.SetCachedResultAsync("knowledge_search", null, OkResult("null-args"));
        var result = await svc.GetCachedResultAsync("knowledge_search", null);

        // Then
        Assert.NotNull(result);
        var text = Assert.IsType<TextContentBlock>(result!.Content![0]);
        Assert.Equal("null-args", text.Text);
    }

    // -------------------------------------------------------------------------
    // Fail-soft: throwing cache must not propagate exceptions
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCachedResultAsync_WhenCacheThrows_ReturnsNullWithoutThrowing()
    {
        // Given: a cache that always throws
        var svc = MakeService(new ThrowingCache());

        // When / Then: no exception
        var result = await svc.GetCachedResultAsync("read_file", Args(("x", "1")));
        Assert.Null(result);
    }

    [Fact]
    public async Task SetCachedResultAsync_WhenCacheThrows_DoesNotThrow()
    {
        // Given
        var svc = MakeService(new ThrowingCache());

        // When / Then
        var call = svc.SetCachedResultAsync("read_file", Args(("x", "1")), OkResult());
        await call;
        Assert.True(call.IsCompletedSuccessfully); // cache failures are swallowed
    }

    // -------------------------------------------------------------------------
    // Inner helpers / fakes
    // -------------------------------------------------------------------------

    private sealed class ThrowingCache : IDistributedCache
    {
        private static Exception Boom() => new InvalidOperationException("redis down");

        public byte[]? Get(string key) => throw Boom();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Boom();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Boom();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Boom();
        public void Refresh(string key) => throw Boom();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Boom();
        public void Remove(string key) => throw Boom();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Boom();
    }

    /// <summary>Records the last TTL used in a SetAsync call.</summary>
    private sealed class TrackingCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _store = new();

        public TimeSpan LastTtl { get; private set; }

        public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _store[key] = value;
            if (options.AbsoluteExpirationRelativeToNow.HasValue)
                LastTtl = options.AbsoluteExpirationRelativeToNow.Value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
    }
}
