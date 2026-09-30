using System.Text.Json;
using KnowledgeHub.Server.Caching;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace KnowledgeHub.Server.Search;

/// <summary>
/// SPEC-20260924-query-expansion-hyde RF-001/RF-002: produces query variants
/// (multi-query / RAG-Fusion) and a hypothetical document (HyDE) to widen
/// recall. Implementations must never throw — fall back to no expansion.
/// </summary>
public interface IQueryExpander
{
    /// <summary>Query reformulations (synonyms, specificity variants). Empty
    /// when disabled/unavailable — callers always include the original query.</summary>
    Task<IReadOnlyList<string>> ExpandQueriesAsync(
        string query, int count, CancellationToken ct = default);

    /// <summary>HyDE: a hypothetical document passage that would answer the
    /// query — embedded for the vector arm only, never shown or cited.</summary>
    Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct = default);
}

/// <summary>No-op expander — expansion disabled.</summary>
public sealed class NoOpExpander : IQueryExpander
{
    public static readonly NoOpExpander Instance = new();
    public Task<IReadOnlyList<string>> ExpandQueriesAsync(string query, int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    public Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// LLM-backed expander. Results cached in <see cref="IDistributedCache"/> keyed
/// <c>expand:{model}:{sha256(query)}:{count}</c> /
/// <c>hyde:{model}:{sha256(query)}</c> for <c>Search:QueryExpansion:Ttl</c>
/// (default 1h). Requires <see cref="IChatClient"/>; absent or failing → empty.
/// </summary>
public sealed class LlmQueryExpander(
    IServiceProvider services,
    IDistributedCache cache,
    IConfiguration configuration,
    ILogger<LlmQueryExpander> logger) : IQueryExpander
{
    private const string ExpandPrompt =
        "Rewrite the user's query into {0} alternative search queries for a knowledge base. " +
        "Vary wording: synonyms, more specific and more general phrasings. " +
        "Return a JSON array of strings only — no explanation.";

    private const string HydePrompt =
        "Write a short passage (2-3 paragraphs) from a knowledge base document " +
        "that would answer the user's question. Write as if it were real content — " +
        "factual tone, plausible specifics. Return only the passage.";

    public async Task<IReadOnlyList<string>> ExpandQueriesAsync(
        string query, int count, CancellationToken ct = default)
    {
        var chat = services.GetService<Assistant.IAssistantChatClientProvider>()
            ?.ForSubtask("expand", services.GetService<IChatClient>())
            ?? services.GetService<IChatClient>();
        if (chat is null)
            return [];

        var model = configuration.GetValue("Chat:Model", "default");
        var key = $"expand:{model}:{CacheKeys.Hash(query)}:{count}";
        var cached = await SafeCache.GetStringAsync(cache, key, logger, ct);
        if (cached is not null)
        {
            System.Diagnostics.Activity.Current?.SetTag("search.expansion.cached", true);
            return JsonSerializer.Deserialize<List<string>>(cached) ?? [];
        }

        try
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, string.Format(ExpandPrompt, count)),
                    new ChatMessage(ChatRole.User, query)
                ],
                new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 256 },
                ct);
            var variants = ParseVariants(response.Text)
                .Where(v => !string.Equals(v, query, StringComparison.OrdinalIgnoreCase))
                .Take(count)
                .ToList();
            if (variants.Count == 0)
                return [];

            await SafeCache.SetJsonAsync(cache, key, variants, null, logger, ct);
            return variants;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Query expansion failed — using original query");
            return [];
        }
    }

    public async Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct = default)
    {
        var chat = services.GetService<Assistant.IAssistantChatClientProvider>()
            ?.ForSubtask("expand", services.GetService<IChatClient>())
            ?? services.GetService<IChatClient>();
        if (chat is null)
            return null;

        var model = configuration.GetValue("Chat:Model", "default");
        var key = $"hyde:{model}:{CacheKeys.Hash(query)}";
        var cached = await SafeCache.GetStringAsync(cache, key, logger, ct);
        if (cached is not null)
        {
            System.Diagnostics.Activity.Current?.SetTag("search.expansion.cached", true);
            return cached;
        }

        try
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, HydePrompt),
                    new ChatMessage(ChatRole.User, query)
                ],
                new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 512 },
                ct);
            var doc = response.Text?.Trim();
            if (string.IsNullOrEmpty(doc) || doc.Length > 8000)
                return null;

            await SafeCache.SetStringAsync(cache, key, doc, null, logger, ct);
            return doc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "HyDE generation failed — using original query vector");
            return null;
        }
    }

    private static IReadOnlyList<string> ParseVariants(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(text[start..(end + 1)])
                ?.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
