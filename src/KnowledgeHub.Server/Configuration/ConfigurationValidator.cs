using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Mcp.Upstream;
using Microsoft.Data.Sqlite;

namespace KnowledgeHub.Server.Configuration;

/// <summary>
/// SPEC-20260914-config-validation: fail-fast validation of the required config
/// blocks (<c>Embeddings</c>, <c>VectorStore</c>, <c>DeepWiki</c>) at startup —
/// before <c>Migrate()</c> — so operators get a clear error instead of deferred
/// "weird behavior".
/// </summary>
public static class ConfigurationValidator
{
    private static readonly HashSet<string> EmbeddingProviders = new(StringComparer.OrdinalIgnoreCase)
        { "deterministic", "ollama", "openai", "onnx" };

    private static readonly HashSet<string> VectorStoreProviders = new(StringComparer.OrdinalIgnoreCase)
        { "sqlite", "sqlite-vec", "postgres" };

    private static readonly HashSet<string> ChatProviders = new(StringComparer.OrdinalIgnoreCase)
        { "none", "ollama", "openai" };

    /// <summary>Throws <see cref="InvalidOperationException"/> listing every problem found.</summary>
    public static void Validate(IConfiguration configuration)
    {
        var problems = new List<string>();
        ValidateDatabase(configuration, problems);
        ValidateEmbeddings(configuration, problems);
        ValidateVectorStore(configuration, problems);
        ValidateDeepWiki(configuration, problems);
        ValidateFirecrawl(configuration, problems);
        ValidateTavily(configuration, problems);
        ValidateContext7(configuration, problems);
        ValidateCache(configuration, problems);
        ValidateChat(configuration, problems);
        ValidateAuth(configuration, problems);
        ValidateRateLimiting(configuration, problems);

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Invalid KnowledgeHub configuration:\n - " + string.Join("\n - ", problems));
    }

    /// <summary>
    /// SPEC-20260916-redis-exposure-risk RF-002: non-fatal configuration
    /// warnings — collected before the host exists, logged at startup.
    /// </summary>
    public static IReadOnlyList<string> CollectWarnings(IConfiguration configuration)
    {
        var warnings = new List<string>();
        var section = configuration.GetSection(CacheOptions.SectionName);
        if (section["Provider"]?.Equals("redis", StringComparison.OrdinalIgnoreCase) == true
            && section["Redis:ConnectionString"]?.Contains("password=", StringComparison.OrdinalIgnoreCase) != true)
        {
            warnings.Add(
                "Cache:Provider=redis with no password= in Cache:Redis:ConnectionString — " + // NOSONAR S2068 — texto de diagnóstico, não credencial
                "an unauthenticated Redis exposes cached search results and embeddings to anyone " +
                "reaching the port. See README 'Redis security' for hardening options.");
        }

        return warnings;
    }

    // SPEC-20260926-unified-database-provider RF-001: Database:Provider selects
    // the single backend for catalog + (by default) vector store.
    private static readonly HashSet<string> DatabaseProviders = new(StringComparer.OrdinalIgnoreCase)
        { "auto", "postgres", "sqlite" };

    private static void ValidateDatabase(IConfiguration cfg, List<string> problems)
    {
        var provider = cfg.GetSection("Database")["Provider"];
        if (!string.IsNullOrWhiteSpace(provider) && !DatabaseProviders.Contains(provider))
            problems.Add($"Database:Provider '{provider}' is invalid (expected: auto | postgres | sqlite)");
        if (provider?.Equals("postgres", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(KnowledgeHub.Server.Data.CatalogDatabase.ResolvePostgresConnectionString(cfg)))
            problems.Add("Database:Provider=postgres requires Database:ConnectionString or POSTGRES_* env vars");
    }

    private static void ValidateEmbeddings(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(EmbeddingOptions.SectionName);
        var provider = section["Provider"];
        if (!string.IsNullOrWhiteSpace(provider) && !EmbeddingProviders.Contains(provider))
            problems.Add($"Embeddings:Provider '{provider}' is invalid (expected: deterministic | ollama | openai | onnx)");

        if (provider is not null && !provider.Equals("deterministic", StringComparison.OrdinalIgnoreCase)
            && !provider.Equals("onnx", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = section["Endpoint"];
            if (!IsHttpUri(endpoint))
                problems.Add($"Embeddings:Endpoint '{endpoint}' is required and must be an absolute http(s) URI when Provider={provider}");
            if (string.IsNullOrWhiteSpace(section["Model"]))
                problems.Add($"Embeddings:Model is required when Provider={provider}");
        }

        // SPEC-20260917-onnx-local-embeddings RF-002: Provider=onnx needs the
        // model artifacts at startup — fail with a clear message, not a crash.
        if (provider?.Equals("onnx", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dir = section["ModelPath"] is { Length: > 0 } p ? p : OnnxEmbeddingProvider.DefaultModelDirectory;
            if (!File.Exists(Path.Combine(dir, OnnxEmbeddingProvider.ModelFileName))
                || !File.Exists(Path.Combine(dir, OnnxEmbeddingProvider.VocabFileName)))
                problems.Add(
                    $"Embeddings:Provider=onnx requires {OnnxEmbeddingProvider.ModelFileName} and " +
                    $"{OnnxEmbeddingProvider.VocabFileName} under '{Path.GetFullPath(dir)}' " +
                    "(set Embeddings:ModelPath or download all-MiniLM-L6-v2 from Hugging Face)");
            if (section["Dimensions"] is { } onnxDims
                && (!int.TryParse(onnxDims, out var od) || od != OnnxEmbeddingProvider.EmbeddingDimensions))
                problems.Add($"Embeddings:Dimensions '{onnxDims}' must be {OnnxEmbeddingProvider.EmbeddingDimensions} when Provider=onnx");
        }

        if (section["Dimensions"] is { } dims && (!int.TryParse(dims, out var d) || d <= 0))
            problems.Add($"Embeddings:Dimensions '{dims}' must be a positive integer");
    }

    private static void ValidateVectorStore(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection("VectorStore");
        var provider = section["Provider"];
        if (!string.IsNullOrWhiteSpace(provider) && !VectorStoreProviders.Contains(provider))
            problems.Add($"VectorStore:Provider '{provider}' is invalid (expected: sqlite | sqlite-vec | postgres)");

        if (provider?.Equals("postgres", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(section["ConnectionString"]))
            problems.Add("VectorStore:ConnectionString is required when VectorStore:Provider=postgres");

        // SPEC-20260923-pgvector-hnsw-scale: numeric knobs must be positive ints.
        foreach (var key in new[] { "HnswThreshold", "HnswM", "HnswEfConstruction", "BatchMax" })
        {
            if (section[$"Postgres:{key}"] is { } v && (!int.TryParse(v, out var n) || n <= 0))
                problems.Add($"VectorStore:Postgres:{key} '{v}' must be a positive integer");
        }

        // SPEC-20260917-sqlite-vec-search CA-002: the extension is native and
        // RID-specific — probe it at startup so a missing lib fails loudly.
        if (provider?.Equals("sqlite-vec", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var probe = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
                probe.Open();
                probe.LoadVector();
            }
            catch (Exception ex)
            {
                problems.Add(
                    "VectorStore:Provider=sqlite-vec requires the sqlite-vec native extension, " +
                    $"which could not be loaded on this runtime: {ex.Message}");
            }
        }
    }

    private static void ValidateDeepWiki(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(DeepWikiOptions.SectionName);
        if (section["Enabled"]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (section["Endpoint"] is { } endpoint && !IsHttpUri(endpoint))
            problems.Add($"DeepWiki:Endpoint '{endpoint}' must be an absolute http(s) URI");

        if (section["PrivateEndpoint"] is { } privateEndpoint && !IsHttpUri(privateEndpoint))
            problems.Add($"DeepWiki:PrivateEndpoint '{privateEndpoint}' must be an absolute http(s) URI");

        if (section["TimeoutSeconds"] is { } t && (!int.TryParse(t, out var ts) || ts <= 0))
            problems.Add($"DeepWiki:TimeoutSeconds '{t}' must be a positive integer");

        if (section["ToolsCacheSeconds"] is { } tc && (!int.TryParse(tc, out var tcs) || tcs <= 0))
            problems.Add($"DeepWiki:ToolsCacheSeconds '{tc}' must be a positive integer");
    }

    private static void ValidateFirecrawl(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(FirecrawlOptions.SectionName);
        if (section["Enabled"]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (section["Endpoint"] is { } endpoint && !IsHttpUri(endpoint))
            problems.Add($"Firecrawl:Endpoint '{endpoint}' must be an absolute http(s) URI");

        if (section["TimeoutSeconds"] is { } t && (!int.TryParse(t, out var ts) || ts <= 0))
            problems.Add($"Firecrawl:TimeoutSeconds '{t}' must be a positive integer");

        if (section["ToolsCacheSeconds"] is { } tc && (!int.TryParse(tc, out var tcs) || tcs <= 0))
            problems.Add($"Firecrawl:ToolsCacheSeconds '{tc}' must be a positive integer");
    }

    private static void ValidateTavily(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(TavilyOptions.SectionName);
        if (section["Enabled"]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (section["Endpoint"] is { } endpoint && !IsHttpUri(endpoint))
            problems.Add($"Tavily:Endpoint '{endpoint}' must be an absolute http(s) URI");

        if (section["TimeoutSeconds"] is { } t && (!int.TryParse(t, out var ts) || ts <= 0))
            problems.Add($"Tavily:TimeoutSeconds '{t}' must be a positive integer");

        if (section["ToolsCacheSeconds"] is { } tc && (!int.TryParse(tc, out var tcs) || tcs <= 0))
            problems.Add($"Tavily:ToolsCacheSeconds '{tc}' must be a positive integer");
    }

    private static void ValidateContext7(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(Context7Options.SectionName);
        if (section["Enabled"]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (section["Endpoint"] is { } endpoint && !IsHttpUri(endpoint))
            problems.Add($"Context7:Endpoint '{endpoint}' must be an absolute http(s) URI");

        if (section["TimeoutSeconds"] is { } t && (!int.TryParse(t, out var ts) || ts <= 0))
            problems.Add($"Context7:TimeoutSeconds '{t}' must be a positive integer");

        if (section["ToolsCacheSeconds"] is { } tc && (!int.TryParse(tc, out var tcs) || tcs <= 0))
            problems.Add($"Context7:ToolsCacheSeconds '{tc}' must be a positive integer");
    }

    private static void ValidateCache(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(CacheOptions.SectionName);
        var provider = section["Provider"];
        if (!string.IsNullOrWhiteSpace(provider)
            && !provider.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !provider.Equals("redis", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Cache:Provider '{provider}' is invalid (expected: memory | redis)");

        if (provider?.Equals("redis", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(section["Redis:ConnectionString"]))
            problems.Add("Cache:Redis:ConnectionString is required when Cache:Provider=redis");
    }

    private static void ValidateChat(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(ChatProviderOptions.SectionName);
        var provider = section["Provider"];
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;
        if (!ChatProviders.Contains(provider))
        {
            problems.Add($"Chat:Provider '{provider}' is invalid (expected: none | ollama | openai)");
            return;
        }

        if (!IsHttpUri(section["Endpoint"]))
            problems.Add($"Chat:Endpoint '{section["Endpoint"]}' is required and must be an absolute http(s) URI when Provider={provider}");
        if (string.IsNullOrWhiteSpace(section["Model"]))
            problems.Add($"Chat:Model is required when Provider={provider}");

        if (section["TimeoutSeconds"] is { } t && (!int.TryParse(t, out var ts) || ts <= 0))
            problems.Add($"Chat:TimeoutSeconds '{t}' must be a positive integer");
        if (section["Temperature"] is { } temp && !double.TryParse(temp, out _))
            problems.Add($"Chat:Temperature '{temp}' must be a number");
        if (section["MaxTokens"] is { } mt && (!int.TryParse(mt, out var m) || m <= 0))
            problems.Add($"Chat:MaxTokens '{mt}' must be a positive integer");
    }

    private static void ValidateAuth(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(AuthOptions.SectionName);
        if (section["AdminInitialPassword"] is { } pw && string.IsNullOrWhiteSpace(pw))
            problems.Add("Auth:AdminInitialPassword must be non-empty when set");
        if (section["LockoutThreshold"] is { } t && (!int.TryParse(t, out var n) || n <= 0))
            problems.Add($"Auth:LockoutThreshold '{t}' must be a positive integer");
        if (section["LockoutMinutes"] is { } m && (!int.TryParse(m, out var mm) || mm <= 0))
            problems.Add($"Auth:LockoutMinutes '{m}' must be a positive integer");
        if (section["MinPasswordLength"] is { } l && (!int.TryParse(l, out var ll) || ll <= 0))
            problems.Add($"Auth:MinPasswordLength '{l}' must be a positive integer");
        if (section["SessionHours"] is { } h && (!int.TryParse(h, out var hh) || hh <= 0))
            problems.Add($"Auth:SessionHours '{h}' must be a positive integer");
    }

    private static void ValidateRateLimiting(IConfiguration cfg, List<string> problems)
    {
        // SPEC-20260923-rate-limiting: numeric knobs must be positive ints.
        var section = cfg.GetSection(RateLimiting.RateLimitOptions.SectionName);
        foreach (var key in new[]
        {
            "LlmPermitLimit", "LlmWindowSeconds", "AnonymousLlmPermitLimit",
            "SyncPermitLimit", "SyncWindowSeconds", "GeneralPermitLimit", "GeneralWindowSeconds"
        })
        {
            if (section[key] is { } v && (!int.TryParse(v, out var n) || n <= 0))
                problems.Add($"RateLimiting:{key} '{v}' must be a positive integer");
        }
    }

    private static bool IsHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
