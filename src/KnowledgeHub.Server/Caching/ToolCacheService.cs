using System.Text.Json;
using KnowledgeHub.Server.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Caching;

/// <summary>
/// Implementation of <see cref="IToolCacheService"/> (SPEC-20260924-redis-cache-and-tool-caching RF-002).
/// Automatically hashes tool invocations, checks index-version validity, and enforces >= 1 hour TTL.
/// </summary>
public sealed class ToolCacheService : IToolCacheService
{
    private readonly IDistributedCache _cache;
    private readonly CacheOptions _options;
    private readonly ILogger<ToolCacheService> _logger;

    public ToolCacheService(
        IDistributedCache cache,
        IOptions<CacheOptions> options,
        ILogger<ToolCacheService> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsCacheable(string toolName, bool isReadOnly)
    {
        if (!_options.ToolCacheEnabled)
            return false;

        if (isReadOnly)
            return true;

        return IsIdempotentReadTool(toolName);
    }

    public async Task<CallToolResult?> GetCachedResultAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken ct = default)
    {
        try
        {
            var canonicalArgs = CanonicalizeArguments(arguments);
            var indexVersion = await IndexVersionToken.GetAsync(_cache, _logger, ct);
            var key = CacheKeys.Tool(toolName, canonicalArgs, indexVersion);

            var cached = await SafeCache.GetStringAsync(_cache, key, _logger, ct);
            if (string.IsNullOrEmpty(cached))
                return null;

            var entry = JsonSerializer.Deserialize<ToolCacheEntry>(cached, JsonSerializerOptions.Web);
            if (entry is null)
                return null;

            _logger.LogDebug("Cache HIT for tool {ToolName} (key {Key})", toolName, key);
            return entry.ToResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to retrieve cached tool result for {ToolName}", toolName);
            return null;
        }
    }

    public async Task SetCachedResultAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        CallToolResult result,
        CancellationToken ct = default)
    {
        // Never cache error responses (transient failures)
        if (result.IsError == true)
            return;

        try
        {
            var canonicalArgs = CanonicalizeArguments(arguments);
            var indexVersion = await IndexVersionToken.GetAsync(_cache, _logger, ct);
            var key = CacheKeys.Tool(toolName, canonicalArgs, indexVersion);

            // Enforce strict minimum TTL of 60 minutes (1 hour) per user requirement
            var ttlMinutes = Math.Max(_options.ToolCacheTtlMinutes, 60);
            var ttl = TimeSpan.FromMinutes(ttlMinutes);

            var entry = ToolCacheEntry.FromResult(result);
            var json = JsonSerializer.Serialize(entry, JsonSerializerOptions.Web);

            await SafeCache.SetStringAsync(_cache, key, json, ttl, _logger, ct);
            _logger.LogDebug("Cached result for tool {ToolName} with TTL {TtlMinutes}m (key {Key})", toolName, ttlMinutes, key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to cache tool result for {ToolName}", toolName);
        }
    }

    private static string CanonicalizeArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "{}";

        var sorted = arguments
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return JsonSerializer.Serialize(sorted, JsonSerializerOptions.Web);
    }

    private static bool IsIdempotentReadTool(string name) =>
        name.StartsWith("read_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("find_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("knowledge_search", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("knowledge_ask", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("context7_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("deepwiki_ask", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// POCO representation of <see cref="CallToolResult"/> for clean JSON serialization in cache stores.
/// </summary>
public sealed class ToolCacheEntry
{
    public bool? IsError { get; set; }
    public List<ToolCacheContentBlock> Content { get; set; } = [];
    public JsonElement? StructuredContent { get; set; }

    public static ToolCacheEntry FromResult(CallToolResult result)
    {
        var entry = new ToolCacheEntry
        {
            IsError = result.IsError,
            StructuredContent = result.StructuredContent
        };

        if (result.Content != null)
        {
            foreach (var block in result.Content)
            {
                if (block is TextContentBlock text)
                {
                    entry.Content.Add(new ToolCacheContentBlock
                    {
                        Type = "text",
                        Text = text.Text
                    });
                }
            }
        }

        return entry;
    }

    public CallToolResult ToResult()
    {
        return new CallToolResult
        {
            IsError = IsError,
            StructuredContent = StructuredContent,
            Content = Content.Select<ToolCacheContentBlock, ContentBlock>(c =>
                new TextContentBlock { Text = c.Text ?? "" }).ToList()
        };
    }
}

public sealed class ToolCacheContentBlock
{
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
}
