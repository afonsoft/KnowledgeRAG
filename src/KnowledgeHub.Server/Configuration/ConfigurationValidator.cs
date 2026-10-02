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
    private const string ProviderKey = "Provider";
    private const string EndpointKey = "Endpoint";
    private const string ModelKey = "Model";
    private const string EnabledKey = "Enabled";
    private const string PrivateEndpointKey = "PrivateEndpoint";
    private const string TimeoutSecondsKey = "TimeoutSeconds";
    private const string ToolsCacheSecondsKey = "ToolsCacheSeconds";
    private const string PostgresProvider = "postgres";
    private const string RedisProvider = "redis";
    private const string FalseValue = "false";

    private static readonly HashSet<string> EmbeddingProviders = new(StringComparer.OrdinalIgnoreCase)
        { "deterministic", "ollama", "openai", "onnx" };

    private static readonly HashSet<string> VectorStoreProviders = new(StringComparer.OrdinalIgnoreCase)
        { "sqlite", "sqlite-vec", PostgresProvider };

    private static readonly HashSet<string> ChatProviders = new(StringComparer.OrdinalIgnoreCase)
        { "none", "ollama", "openai" };

    /// <summary>Throws <see cref="InvalidOperationException"/> listing every problem found.</summary>
    public static void Validate(IConfiguration configuration)
    {
        var problems = new List<string>();
        ValidateDatabase(configuration, problems);
        ValidateEmbeddings(configuration, problems);
        ValidateVectorStore(configuration, problems);
        ValidateUpstreamIntegration(configuration, DeepWikiOptions.SectionName, problems);
        ValidateUpstreamIntegration(configuration, FirecrawlOptions.SectionName, problems);
        ValidateUpstreamIntegration(configuration, TavilyOptions.SectionName, problems);
        ValidateUpstreamIntegration(configuration, Context7Options.SectionName, problems);
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
        if (section[ProviderKey]?.Equals(RedisProvider, StringComparison.OrdinalIgnoreCase) == true
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
        { "auto", PostgresProvider, "sqlite" };

    private static void ValidateDatabase(IConfiguration cfg, List<string> problems)
    {
        var provider = cfg.GetSection("Database")["Provider"];
        if (!string.IsNullOrWhiteSpace(provider) && !DatabaseProviders.Contains(provider))
            problems.Add($"Database:Provider '{provider}' is invalid (expected: auto | postgres | sqlite)");
        if (provider?.Equals(PostgresProvider, StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(KnowledgeHub.Server.Data.CatalogDatabase.ResolvePostgresConnectionString(cfg)))
            problems.Add("Database:Provider=postgres requires Database:ConnectionString or POSTGRES_* env vars");
    }

    private static void ValidateEmbeddings(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(EmbeddingOptions.SectionName);
        var provider = section[ProviderKey];
        if (!string.IsNullOrWhiteSpace(provider) && !EmbeddingProviders.Contains(provider))
            problems.Add($"Embeddings:Provider '{provider}' is invalid (expected: deterministic | ollama | openai | onnx)");

        if (RequiresRemoteEndpoint(provider))
            ValidateRemoteProvider(section, provider!, problems);

        // SPEC-20260917-onnx-local-embeddings RF-002: Provider=onnx needs the
        // model artifacts at startup — fail with a clear message, not a crash.
        if (provider?.Equals("onnx", StringComparison.OrdinalIgnoreCase) == true)
            ValidateOnnxArtifacts(section, problems);

        RequirePositiveInt(section, "Dimensions", "Embeddings", problems);
    }

    private static bool RequiresRemoteEndpoint(string? provider)
        => provider is not null
            && !provider.Equals("deterministic", StringComparison.OrdinalIgnoreCase)
            && !provider.Equals("onnx", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRemoteProvider(IConfigurationSection section, string provider, List<string> problems)
    {
        var endpoint = section[EndpointKey];
        if (!IsHttpUri(endpoint))
            problems.Add($"Embeddings:Endpoint '{endpoint}' is required and must be an absolute http(s) URI when Provider={provider}");
        if (string.IsNullOrWhiteSpace(section[ModelKey]))
            problems.Add($"Embeddings:Model is required when Provider={provider}");
    }

    private static void ValidateOnnxArtifacts(IConfigurationSection section, List<string> problems)
    {
        var dir = section["ModelPath"] is { Length: > 0 } p ? p : OnnxEmbeddingProvider.DefaultModelDirectory;
        if (!File.Exists(Path.Join(dir, OnnxEmbeddingProvider.ModelFileName))
            || !File.Exists(Path.Join(dir, OnnxEmbeddingProvider.VocabFileName)))
            problems.Add(
                $"Embeddings:Provider=onnx requires {OnnxEmbeddingProvider.ModelFileName} and " +
                $"{OnnxEmbeddingProvider.VocabFileName} under '{Path.GetFullPath(dir)}' " +
                "(set Embeddings:ModelPath or download all-MiniLM-L6-v2 from Hugging Face)");
        if (section["Dimensions"] is { } onnxDims
            && (!int.TryParse(onnxDims, out var od) || od != OnnxEmbeddingProvider.EmbeddingDimensions))
            problems.Add($"Embeddings:Dimensions '{onnxDims}' must be {OnnxEmbeddingProvider.EmbeddingDimensions} when Provider=onnx");
    }

    private static void ValidateVectorStore(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection("VectorStore");
        var provider = section[ProviderKey];
        if (!string.IsNullOrWhiteSpace(provider) && !VectorStoreProviders.Contains(provider))
            problems.Add($"VectorStore:Provider '{provider}' is invalid (expected: sqlite | sqlite-vec | postgres)");

        if (provider?.Equals(PostgresProvider, StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(section["ConnectionString"]))
            problems.Add("VectorStore:ConnectionString is required when VectorStore:Provider=postgres");

        // SPEC-20260923-pgvector-hnsw-scale: numeric knobs must be positive ints.
        foreach (var key in new[] { "HnswThreshold", "HnswM", "HnswEfConstruction", "BatchMax" })
            RequirePositiveInt(section, $"Postgres:{key}", "VectorStore", problems);

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

    private static void ValidateUpstreamIntegration(IConfiguration cfg, string sectionName, List<string> problems)
    {
        var section = cfg.GetSection(sectionName);
        if (section[EnabledKey]?.Equals(FalseValue, StringComparison.OrdinalIgnoreCase) == true)
            return;

        RequireHttpUri(section, EndpointKey, sectionName, problems);
        RequireHttpUri(section, PrivateEndpointKey, sectionName, problems);
        RequirePositiveInt(section, TimeoutSecondsKey, sectionName, problems);
        RequirePositiveInt(section, ToolsCacheSecondsKey, sectionName, problems);
    }

    private static void RequireHttpUri(IConfigurationSection section, string key, string prefix, List<string> problems)
    {
        if (section[key] is { } value && !IsHttpUri(value))
            problems.Add($"{prefix}:{key} '{value}' must be an absolute http(s) URI");
    }

    private static void RequirePositiveInt(IConfigurationSection section, string key, string prefix, List<string> problems)
    {
        if (section[key] is { } value && (!int.TryParse(value, out var n) || n <= 0))
            problems.Add($"{prefix}:{key} '{value}' must be a positive integer");
    }

    private static void ValidateCache(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(CacheOptions.SectionName);
        var provider = section[ProviderKey];
        if (!string.IsNullOrWhiteSpace(provider)
            && !provider.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !provider.Equals(RedisProvider, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Cache:Provider '{provider}' is invalid (expected: memory | redis)");

        if (provider?.Equals(RedisProvider, StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(section["Redis:ConnectionString"]))
            problems.Add("Cache:Redis:ConnectionString is required when Cache:Provider=redis");
    }

    private static void ValidateChat(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(ChatProviderOptions.SectionName);
        var provider = section[ProviderKey];
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;
        if (!ChatProviders.Contains(provider))
        {
            problems.Add($"Chat:Provider '{provider}' is invalid (expected: none | ollama | openai)");
            return;
        }

        if (!IsHttpUri(section[EndpointKey]))
            problems.Add($"Chat:Endpoint '{section[EndpointKey]}' is required and must be an absolute http(s) URI when Provider={provider}");
        if (string.IsNullOrWhiteSpace(section[ModelKey]))
            problems.Add($"Chat:Model is required when Provider={provider}");

        RequirePositiveInt(section, TimeoutSecondsKey, "Chat", problems);
        if (section["Temperature"] is { } temp && !double.TryParse(temp, out _))
            problems.Add($"Chat:Temperature '{temp}' must be a number");
        RequirePositiveInt(section, "MaxTokens", "Chat", problems);
    }

    private static void ValidateAuth(IConfiguration cfg, List<string> problems)
    {
        var section = cfg.GetSection(AuthOptions.SectionName);
        if (section["AdminInitialPassword"] is { } pw && string.IsNullOrWhiteSpace(pw))
            problems.Add("Auth:AdminInitialPassword must be non-empty when set");
        RequirePositiveInt(section, "LockoutThreshold", "Auth", problems);
        RequirePositiveInt(section, "LockoutMinutes", "Auth", problems);
        RequirePositiveInt(section, "MinPasswordLength", "Auth", problems);
        RequirePositiveInt(section, "SessionHours", "Auth", problems);
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
            RequirePositiveInt(section, key, "RateLimiting", problems);
    }

    private static bool IsHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
