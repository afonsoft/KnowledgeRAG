using KnowledgeHub.Server.Mcp.Upstream;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// Integration credential management (SPEC-20260916-firecrawl-mcp-proxy
/// RF-005/RF-006): masked listing plus save/remove per provider. Secrets are
/// written to the encrypted store and never echoed back — GET only exposes
/// hasKey + last-4 hint + source.
/// </summary>
public static class SettingsEndpoints
{
    private const string BodyRequired = "body is required";
    /// <summary>Mapeia o grupo /api/settings: keys de integrações e configuração de chat.</summary>
    public static RouteGroupBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings");

        MapIntegrationEndpoints(group);
        MapChatEndpoints(group);
        MapEmbeddingEndpoints(group);
        MapGraphEndpoints(group);
        MapResilienceEndpoints(group);
        MapAssistantEndpoints(group);
        MapCacheEndpoints(group);
        MapDiagnosticsEndpoints(group);

        return group;
    }

    private static void MapIntegrationEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/integrations", async (
            IIntegrationSecretStore store,
            IIntegrationStateService state,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var items = new List<IntegrationSettingsDto>();
            foreach (var provider in IntegrationProviders.All)
                items.Add(await DescribeAsync(provider, store, state, cfg, ct));
            return Results.Ok(new IntegrationSettingsResponse { Integrations = items });
        });

        // SPEC-20260926-integration-toggle: runtime on/off per integration —
        // disabled providers contribute no tools to the MCP catalog and their
        // upstream session is reset.
        group.MapPut("/integrations/{provider}/enabled", async (
            string provider,
            SetIntegrationEnabledRequest? body,
            IIntegrationStateService state,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!IntegrationProviders.All.Contains(provider))
                return Results.NotFound(new { error = $"unknown provider '{provider}'" });
            if (body is null)
                return Results.BadRequest(new { error = "enabled is required" });

            await state.SetEnabledAsync(provider, body.Enabled, ct);
            await ResetProviderAsync(provider, services, ct);
            return Results.NoContent();
        });

        group.MapPut("/integrations/{provider}", async (
            string provider,
            SetIntegrationKeyRequest? body,
            IIntegrationSecretStore store,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!IntegrationProviders.All.Contains(provider))
                return Results.NotFound(new { error = $"unknown provider '{provider}'" });

            var apiKey = body?.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.BadRequest(new { error = "apiKey is required" });
            if (provider == IntegrationProviders.Firecrawl && !apiKey.StartsWith("fc-", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Firecrawl API keys start with 'fc-'" });
            if (provider == IntegrationProviders.Tavily && !apiKey.StartsWith("tvly-", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Tavily API keys start with 'tvly-'" });
            if (provider == IntegrationProviders.Context7 && !apiKey.StartsWith("ctx7sk-", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Context7 API keys start with 'ctx7sk-'" });

            await store.SetAsync(provider, apiKey, ct);
            await ResetProviderAsync(provider, services, ct);
            return Results.NoContent();
        });

        group.MapDelete("/integrations/{provider}", async (
            string provider,
            IIntegrationSecretStore store,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!IntegrationProviders.All.Contains(provider))
                return Results.NotFound(new { error = $"unknown provider '{provider}'" });

            await store.RemoveAsync(provider, ct);
            await ResetProviderAsync(provider, services, ct);
            return Results.NoContent();
        });

    }

    private static void MapChatEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260916-settings-chat-config RF-004: chat provider (endpoint +
        // model + API key) editable from /settings; effective immediately via
        // the service's Invalidate() — no restart.
        group.MapGet("/chat", async (
            IChatSettingsService chat,
            CancellationToken ct) =>
            Results.Ok(await chat.DescribeAsync(ct)));

        group.MapPut("/chat", async (
            SaveChatSettingsRequest? body,
            IChatSettingsService chat,
            CancellationToken ct) =>
        {
            var endpoint = body?.Endpoint?.Trim();
            var model = body?.Model?.Trim();
            if (string.IsNullOrWhiteSpace(endpoint) || !IsHttpUri(endpoint))
                return Results.BadRequest(new { error = "endpoint must be an absolute http(s) URI" });
            if (string.IsNullOrWhiteSpace(model))
                return Results.BadRequest(new { error = "model is required" });

            await chat.SaveAsync(endpoint, model, body!.ApiKey, ct);
            return Results.NoContent();
        });

        group.MapDelete("/chat/apikey", async (
            IChatSettingsService chat,
            CancellationToken ct) =>
        {
            await chat.RemoveKeyAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/chat", async (
            IChatSettingsService chat,
            CancellationToken ct) =>
        {
            await chat.ClearAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/chat/test", async (
            TestChatConnectionRequest? body,
            IChatSettingsService chat,
            CancellationToken ct) =>
        {
            var endpoint = body?.Endpoint?.Trim();
            if (!string.IsNullOrEmpty(endpoint) && !IsHttpUri(endpoint))
                return Results.BadRequest(new { error = "endpoint must be an absolute http(s) URI" });

            return Results.Ok(await chat.TestAsync(body ?? new TestChatConnectionRequest(), ct));
        });

    }

    private static void MapEmbeddingEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260926-settings-ux-embeddings RF-004: embedding/indexing
        // provider (provider + endpoint + model + dims + chunking + API key)
        // editable from /settings; the resolver swaps the live provider on
        // signature change — no restart, but warn about dims/model drift.
        var embeddingProviders = new[] { "deterministic", "ollama", "openai", "onnx" };
        group.MapGet("/embeddings", async (
            IEmbeddingSettingsService emb,
            Embeddings.IEmbeddingProviderResolver resolver,
            VectorStore.IVectorStore store,
            CancellationToken ct) =>
        {
            var dto = await emb.DescribeAsync(ct);
            // SPEC-20260926-embeddings-runtime-coherence RF-002: a broken stored
            // config (e.g. missing ONNX model) must never sink the GET — the
            // editor needs to render to offer "Restaurar ambiente".
            string? stampedId = null, providerError = null;
            try
            {
                stampedId = resolver.Current.ModelId;
            }
            catch (Exception ex)
            {
                var b = ex.GetBaseException();
                providerError = $"{b.GetType().Name}: {b.Message}";
                if (providerError.Length > 300) providerError = providerError[..300];
            }
            return Results.Ok(dto with
            {
                StampedModelId = stampedId,
                ProviderError = providerError,
                StoreDimensions = store.Dimensions
            });
        });

        group.MapPut("/embeddings", async (
            SaveEmbeddingSettingsRequest? body,
            IEmbeddingSettingsService emb,
            VectorStore.IVectorStore store,
            CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = BodyRequired });
            var provider = body.Provider?.Trim().ToLowerInvariant();
            if (!embeddingProviders.Contains(provider))
                return Results.BadRequest(new { error = $"provider must be one of: {string.Join(", ", embeddingProviders)}" });
            if (!string.IsNullOrWhiteSpace(body.Endpoint) && !IsHttpUri(body.Endpoint.Trim()))
                return Results.BadRequest(new { error = "endpoint must be an absolute http(s) URI" });
            if (body.Dimensions is < 64 or > 4096)
                return Results.BadRequest(new { error = "dimensions must be 64..4096" });
            // SPEC-20260926-embeddings-runtime-coherence RF-006: overlap cap —
            // >~40% of the chunk degrades piece coherence; max 2000 regardless
            // of maxTokens, and always < maxTokens.
            if (body.OverlapTokens is < 0 or > 2000)
                return Results.BadRequest(new { error = "overlapTokens must be 0..2000" });
            if (body.MaxTokens is < 100 or > 4000)
                return Results.BadRequest(new { error = "maxTokens must be 100..4000" });
            if (body.MaxTokens is { } mt && body.OverlapTokens is { } ov && ov >= mt)
                return Results.BadRequest(new { error = "overlapTokens must be smaller than maxTokens" });
            // SPEC-20260926-embeddings-runtime-coherence RF-001: vector schemas
            // (sqlite-vec/pgvector) are compiled at startup — dims ≠ store dims
            // can never be accepted until env + restart + reindex.
            if (store.Dimensions is { } storeDims && body.Dimensions != storeDims)
                return Results.BadRequest(new
                {
                    error = $"dimensions {body.Dimensions} != vector store {storeDims} — " +
                            "the index schema is fixed at startup; set Embeddings:Dimensions " +
                            "in the environment, restart, then run reindex"
                });
            // SPEC-20260926-embeddings-runtime-coherence RF-002: fail fast on an
            // ONNX path that cannot possibly load instead of persisting a
            // config that breaks the provider at runtime.
            if (provider == "onnx" && !string.IsNullOrWhiteSpace(body.ModelPath))
            {
                var dir = body.ModelPath.Trim();
                if (!Directory.Exists(dir))
                    return Results.BadRequest(new { error = $"modelPath '{dir}' does not exist" });
            }
            // RF-001 (SPEC-20260926-embeddings-swap-safety): validate against the
            // MODEL's actual output width — the store check above only compares
            // request vs schema; a 512-dim ONNX file accepted under
            // Dimensions=384 would silently produce garbage vectors.
            if (provider == "onnx" && body.Dimensions is { } requestedDims)
            {
                Embeddings.OnnxEmbeddingProvider? probe = null;
                try
                {
                    probe = Embeddings.OnnxEmbeddingProvider.Load(body.ModelPath);
                }
                catch (Embeddings.EmbeddingProviderException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // RF-603: a corrupt model.onnx throws native ONNX/tokenizer
                    // exceptions, not EmbeddingProviderException — still a 400,
                    // never a 500 on a settings save.
                    return Results.BadRequest(new { error = $"onnx model failed to load: {ex.GetBaseException().Message}" });
                }
                using (probe)
                {
                    if (probe.Dimensions != requestedDims)
                        return Results.BadRequest(new
                        {
                            error = $"dimensions {requestedDims} != onnx model output {probe.Dimensions} — " +
                                    "the model file defines the vector size"
                        });
                }
            }

            await emb.SaveAsync(body with { Provider = provider! }, ct);
            return Results.NoContent();
        });

        group.MapDelete("/embeddings/apikey", async (
            IEmbeddingSettingsService emb,
            CancellationToken ct) =>
        {
            await emb.RemoveKeyAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/embeddings", async (
            IEmbeddingSettingsService emb,
            CancellationToken ct) =>
        {
            await emb.ClearAsync(ct);
            return Results.NoContent();
        });

    }

    private static void MapGraphEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260923-graph-settings-ui RF-004: GraphRAG switch + tuning knobs,
        // editable from /settings; effective immediately via the service's
        // Invalidate() — no restart.
        group.MapGet("/graph", async (
            IGraphSettingsService graph,
            CancellationToken ct) =>
            Results.Ok(await graph.DescribeAsync(ct)));

        group.MapPut("/graph", async (
            SaveGraphSettingsRequest? body,
            IGraphSettingsService graph,
            CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = BodyRequired });
            if (body.MaxChunksPerSync is < 1 or > 10_000)
                return Results.BadRequest(new { error = "maxChunksPerSync must be 1..10000" });
            if (body.MaxChunkChars is < 200 or > 50_000)
                return Results.BadRequest(new { error = "maxChunkChars must be 200..50000" });
            if (body.MaxResults is < 10 or > 10_000)
                return Results.BadRequest(new { error = "maxResults must be 10..10000" });

            await graph.SaveAsync(body, ct);
            return Results.NoContent();
        });

        group.MapDelete("/graph", async (
            IGraphSettingsService graph,
            CancellationToken ct) =>
        {
            await graph.ClearAsync(ct);
            return Results.NoContent();
        });

    }

    private static void MapResilienceEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260928-resilience-tool-fallback-wiring RF-004: fallback policy
        // (mode/budget/alternates/capabilities), editable from /settings;
        // effective immediately via the service's Invalidate() — no restart.
        group.MapGet("/resilience", async (
            Settings.IResilienceSettingsService resilience,
            CancellationToken ct) =>
            Results.Ok(await resilience.DescribeAsync(ct)));

        group.MapPut("/resilience", async (
            SaveResilienceSettingsRequest? body,
            Settings.IResilienceSettingsService resilience,
            CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = BodyRequired });
            if (body.Mode is not ("disabled" or "observe" or "enforce"))
                return Results.BadRequest(new { error = "mode must be disabled|observe|enforce" });
            if (body.MaxFallbackAttempts is < 1 or > 5)
                return Results.BadRequest(new { error = "maxFallbackAttempts must be 1..5" });
            if (body.ChatFallbacks is { Count: > 10 })
                return Results.BadRequest(new { error = "chatFallbacks capped at 10" });

            await resilience.SaveAsync(body, ct);
            return Results.NoContent();
        });

        group.MapDelete("/resilience", async (
            Settings.IResilienceSettingsService resilience,
            CancellationToken ct) =>
        {
            await resilience.ClearAsync(ct);
            return Results.NoContent();
        });

    }

    private static void MapAssistantEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260929-a2a-assistant-delegation RF-001: low-cost assistant provider
        // (local OpenAI-compatible or remote A2A agent) routed to cheap sub-tasks.
        group.MapGet("/assistant", async (
            Settings.IAssistantSettingsService assistant,
            CancellationToken ct) =>
            Results.Ok(await assistant.DescribeAsync(ct)));

        group.MapPut("/assistant", async (
            SaveAssistantSettingsRequest? body,
            Settings.IAssistantSettingsService assistant,
            CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = BodyRequired });
            if (body.Mode is not ("local" or "remote"))
                return Results.BadRequest(new { error = "mode must be local|remote" });
            if (body.Enabled && string.IsNullOrWhiteSpace(body.Endpoint))
                return Results.BadRequest(new { error = "endpoint is required when enabled" });

            await assistant.SaveAsync(body, ct);
            return Results.NoContent();
        });

        group.MapDelete("/assistant/apikey", async (
            Settings.IAssistantSettingsService assistant,
            CancellationToken ct) =>
        {
            await assistant.RemoveKeyAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/assistant", async (
            Settings.IAssistantSettingsService assistant,
            CancellationToken ct) =>
        {
            await assistant.ClearAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/assistant/test", async (
            TestAssistantConnectionRequest? body,
            Settings.IAssistantSettingsService assistant,
            CancellationToken ct) =>
            Results.Ok(await assistant.TestAsync(body ?? new TestAssistantConnectionRequest(), ct)));

    }

    private static void MapCacheEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260924-redis-cache-and-tool-caching RF-003/RF-004: Cache inspection and clear
        group.MapGet("/cache", async (
            Caching.ICacheManagerService cacheMgr,
            CancellationToken ct) =>
            Results.Ok(await cacheMgr.GetStatsAsync(ct)));

        group.MapPost("/cache/clear", async (
            Caching.ICacheManagerService cacheMgr,
            CancellationToken ct) =>
            Results.Ok(await cacheMgr.ClearAllAsync(ct)));

        // SPEC-20260926-settings-ux-embeddings RF-003: per-key eviction —
        // removes the real entry (L1+L2) and the tracked record.
        // SPEC-20260926-cache-key-consistency RF-002: failures surface as
        // errors — never 204 for a key still present in the cache.
        group.MapDelete("/cache/keys/{*key}", async (
            string key,
            Caching.ICacheManagerService cacheMgr,
            CancellationToken ct) =>
        {
            var result = await cacheMgr.RemoveEntryAsync(key, ct);
            if (!result.Tracked)
                return Results.NotFound(new { error = $"key '{key}' not tracked" });
            if (result.Error is { } err)
                return Results.Problem(title: "cache key removal failed", detail: err, statusCode: 502);
            return Results.NoContent();
        });

    }

    private static void MapDiagnosticsEndpoints(RouteGroupBuilder group)
    {
        // SPEC-20260926-settings-tabs-database-metrics RF-002: storage snapshot
        // for the "Banco de Dados" tab — provider, sizes, PRAGMAs, entity
        // counts, migrations + vector store diagnostics (fail-soft per section).
        group.MapGet("/database", async (
            Data.KnowledgeHubDbContext db,
            IConfiguration cfg,
            VectorStore.IVectorStore vectors,
            CancellationToken ct) =>
            Results.Ok(await DatabaseStatsBuilder.BuildAsync(db, cfg, vectors, ct)));

        // SPEC-20260925-runtime-log-level RF-002/RF-003: runtime log level with
        // auto-reset — a Debug session expires instead of filling the disk.
        group.MapGet("/log-level", (Telemetry.LogLevelControl control) =>
        {
            var (level, resetAt) = control.Current();
            return Results.Ok(new { level, autoResetAt = resetAt, configuredDefault = control.ConfiguredDefault.ToString() });
        });

        group.MapPut("/log-level", (
            SetLogLevelRequest? body,
            Telemetry.LogLevelControl control,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (body is null
                || !Enum.TryParse<Serilog.Events.LogEventLevel>(body.Level, ignoreCase: true, out var level))
                return Results.BadRequest(new { error = "level must be one of Verbose|Debug|Information|Warning|Error|Fatal" });

            var minutes = Math.Clamp(body.Minutes ?? 15, 0, 120);
            var (current, resetAt) = control.Set(level, minutes);
            // RF audit: the log line carries the caller identity.
            loggerFactory.CreateLogger("Settings.LogLevel")
                .LogInformation("log level changed to {Level} (auto-reset {AutoReset}, caller {Caller})",
                    current, resetAt, http.User.Identity?.Name ?? "anonymous");
            return Results.Ok(new
            {
                level = current,
                autoResetAt = resetAt,
                configuredDefault = control.ConfiguredDefault.ToString()
            });
        });

    }
    /// <summary>Retorna true quando o valor é uma URI absoluta http(s).</summary>
    private static bool IsHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Monta o DTO mascarado da integração (key do store → env), sem expor o segredo.</summary>
    private static async Task<IntegrationSettingsDto> DescribeAsync(
        string provider, IIntegrationSecretStore store, IIntegrationStateService state,
        IConfiguration cfg, CancellationToken ct)
    {
        var info = await store.GetInfoAsync(provider, ct);
        var envKey = cfg[$"{ConfigSection(provider)}:ApiKey"];

        var (hasKey, hint, source) = info is not null
            ? (true, MaskHint(provider, info.KeyHint), "store")
            : !string.IsNullOrWhiteSpace(envKey)
                ? (true, MaskHint(provider, envKey.Length >= 4 ? envKey[^4..] : envKey), "env")
                : (false, null, "none");

        return new IntegrationSettingsDto
        {
            Provider = provider,
            DisplayName = provider switch
            {
                IntegrationProviders.Firecrawl => "Firecrawl",
                IntegrationProviders.DeepWiki => "DeepWiki",
                IntegrationProviders.Tavily => "Tavily",
                IntegrationProviders.Context7 => "Context7",
                _ => provider
            },
            HasKey = hasKey,
            Enabled = await state.IsEnabledAsync(provider, ct),
            KeyHint = hint,
            Source = source,
            Note = provider switch
            {
                IntegrationProviders.Firecrawl =>
                    "Expõe as tools oficiais do Firecrawl (scrape, search, crawl…) no MCP interno.",
                IntegrationProviders.DeepWiki =>
                    "Com key, o DeepWiki usa o endpoint privado (mcp.devin.ai) — acesso a repos privados.",
                IntegrationProviders.Tavily =>
                    "Expõe as tools oficiais do Tavily (search, extract, map, crawl, research) no MCP interno.",
                IntegrationProviders.Context7 =>
                    "Expõe as tools oficiais do Context7 (resolve-library-id, query-docs) no MCP interno.",
                _ => null
            }
        };
    }

    /// <summary>Mapeia o slug da integração para a seção de configuração correspondente.</summary>
    private static string ConfigSection(string provider) => provider switch
    {
        IntegrationProviders.Firecrawl => "Firecrawl",
        IntegrationProviders.DeepWiki => "DeepWiki",
        IntegrationProviders.Tavily => "Tavily",
        IntegrationProviders.Context7 => "Context7",
        _ => provider
    };

    /// <summary>Formata o hint mascarado com o prefixo do provider quando aplicável.</summary>
    private static string MaskHint(string provider, string last4) =>
        provider == IntegrationProviders.Firecrawl ? $"fc-••••{last4}"
            : provider == IntegrationProviders.Tavily ? $"tvly-••••{last4}"
            : provider == IntegrationProviders.Context7 ? $"ctx7sk-••••{last4}"
            : $"••••{last4}";

    /// <summary>Descarta a sessão upstream do provider para a próxima chamada usar a
    /// nova credencial efetiva (e, no DeepWiki, o novo endpoint), depois notifica o
    /// catálogo — a lista efetiva de tools mudou.</summary>
    private static async Task ResetProviderAsync(string provider, IServiceProvider services, CancellationToken ct)
    {
        switch (provider)
        {
            case IntegrationProviders.Firecrawl:
                await services.GetRequiredService<FirecrawlUpstreamClient>().ResetAsync();
                services.GetRequiredService<FirecrawlToolsProvider>().InvalidateToolsCache();
                break;
            case IntegrationProviders.DeepWiki:
                await services.GetRequiredService<DeepWikiUpstreamClient>().ResetAsync();
                services.GetRequiredService<DeepWikiToolsProvider>().InvalidateToolsCache();
                break;
            case IntegrationProviders.Tavily:
                await services.GetRequiredService<TavilyUpstreamClient>().ResetAsync();
                services.GetRequiredService<TavilyToolsProvider>().InvalidateToolsCache();
                break;
            case IntegrationProviders.Context7:
                await services.GetRequiredService<Context7UpstreamClient>().ResetAsync();
                services.GetRequiredService<Context7ToolsProvider>().InvalidateToolsCache();
                break;
        }
        await services.GetRequiredService<Mcp.IToolCatalogChangeNotifier>().NotifyToolsChangedAsync(ct);
    }
}

/// <summary>PUT /api/settings/log-level body (SPEC-20260925-runtime-log-level).</summary>
public sealed record SetLogLevelRequest(string? Level, int? Minutes);
