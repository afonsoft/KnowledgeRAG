using System.Text.Json;
using KnowledgeHub.McpEngine.Activity;
using Microsoft.AspNetCore.DataProtection;
using KnowledgeHub.Server.BackgroundServices;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Evaluation;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KnowledgeHub.Server;

/// <summary>Server-side DI composition (persistence, embeddings, search, ingestion).</summary>
public static class KnowledgeHubServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeHubServer(this IServiceCollection services, IConfiguration configuration)
    {
        // SPEC-20260922-per-key-integration-secrets RF-000: caller identity for
        // per-key settings/upstream resolution — previously unregistered, which
        // silently disabled every per-key code path.
        services.AddHttpContextAccessor();

        // SPEC-20260926-unified-database-provider RF-001: one backend for the
        // catalog — Postgres when Database:Provider=auto/postgres resolves and
        // probes OK, SQLite otherwise. Resolved once here (env vars are already
        // visible to the builder config; late ConfigureWebHost overrides — the
        // test path — resolve to sqlite anyway since they never set POSTGRES_*).
        var catalog = CatalogDatabase.Resolve(configuration);
        services.AddSingleton(catalog);

        AddDataAccess(services, catalog);
        AddEmbeddings(services);
        AddEvaluation(services);
        AddUpstreamHttpClients(services);
        AddSourceConnectors(services);
        AddChatProviders(services);
        AddAgentServices(services);
        AddVectorStore(services);
        AddRetrieval(services);
        AddGraph(services);
        AddIngestion(services);
        AddToolCatalog(services);
        AddSecrets(services, configuration);
        AddUpstreamTools(services);
        AddCaching(services, configuration);
        AddMcpServer(services);
        AddTelemetry(services, configuration);

        return services;
    }

    private static void AddDataAccess(IServiceCollection services, CatalogDatabase catalog)
    {
        // SPEC-20260916-performance-memory-cache RF-006: pooled contexts — one
        // DbContext allocation per request instead of a fresh graph each time.
        if (catalog.IsPostgres)
        {
            // Postgres migrations annotate the subclass — EF resolves the right
            // set from the concrete type at Migrate() time.
            services.AddDbContextPool<KnowledgeHubDbContext, PostgresKnowledgeHubDbContext>(
                (sp, o) => o.UseNpgsql(catalog.PostgresConnectionString)
                    .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>());
            services.AddDbContextFactory<KnowledgeHubDbContext>((sp, o) =>
                o.UseNpgsql(catalog.PostgresConnectionString)
                    .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>());
        }
        else
        {
            services.AddDbContextPool<KnowledgeHubDbContext>((sp, o) =>
                o.UseSqlite($"Data Source={DatabasePath.Resolve(sp.GetRequiredService<IConfiguration>())}")
                    .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>());
            services.AddDbContextFactory<KnowledgeHubDbContext>((sp, o) =>
                o.UseSqlite($"Data Source={DatabasePath.Resolve(sp.GetRequiredService<IConfiguration>())}")
                    .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>());
        }
    }

    private static void AddEmbeddings(IServiceCollection services)
    {
        services.AddOptions<EmbeddingOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(EmbeddingOptions.SectionName).Bind(options));

        // SPEC-20260926-settings-ux-embeddings RF-004: effective options come
        // from the EmbeddingSettings store over env. Consumers still inject
        // IEmbeddingProvider — a delegating facade forwards to resolver.Current
        // so /settings edits swap the provider without restart.
        services.AddSingleton<Settings.IEmbeddingSettingsService>(sp => new Settings.EmbeddingSettingsService(
            sp.GetRequiredService<IOptions<Embeddings.EmbeddingOptions>>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<Settings.IIntegrationSecretStore>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<Settings.EmbeddingSettingsService>>(),
            sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<Caching.ICacheInvalidationBus>()));
        services.AddSingleton<IEmbeddingProviderResolver, EmbeddingProviderResolver>();
        services.AddSingleton<IEmbeddingProvider>(sp =>
            new DelegatingEmbeddingProvider(sp.GetRequiredService<IEmbeddingProviderResolver>()));
    }

    private static void AddEvaluation(IServiceCollection services)
    {
        // SPEC-20260927-rag-evaluation-triad-metrics: RAG Quality Triad evaluator.
        services.AddOptions<Evaluation.RagEvaluationOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(Evaluation.RagEvaluationOptions.SectionName).Bind(options));
        services.AddSingleton<Evaluation.IRagTriadEvaluator, Evaluation.RagTriadEvaluator>();
        services.AddSingleton<Evaluation.IRagEvaluationEnqueuer, Evaluation.RagEvaluationEnqueuer>();
        services.AddHostedService<Evaluation.EvaluationWorker>();

        // SPEC-20260923-eval-harness: read-only retrieval-quality runner.
        services.AddScoped<Eval.EvalRunner>();
        // SPEC-20260924-eval-regression-gate RF-003: scheduled eval + gate alerts.
        services.AddHostedService<Eval.EvalScheduleService>();
    }

    private static void AddUpstreamHttpClients(IServiceCollection services)
    {
        // SPEC-20260923-agent-runtime-hardening RF-004: standard resilience
        // pipeline (retry 3× exp+jitter on transient failures, per-attempt +
        // total timeouts, circuit breaker). Client.Timeout moves to Infinite so
        // the pipeline owns timing — the per-attempt timeout preserves the old
        // client-level bound. All upstream calls are idempotent reads/inference.
        services.AddHttpClient("embeddings", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(100);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(6);
                // Sampling window must be ≥ 2× the attempt timeout.
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4);
            });
        // SPEC-20261001-a2a-task-durability RF-003: A2A push webhooks ride the
        // same SSRF egress policy as connector traffic (no auto-redirects,
        // private-network block unless opted in).
        services.AddHttpClient("a2a-push", c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient("webpage", c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
            });
        services.AddHttpClient("notion", c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
            });
        // MCP upstream proxies (SPEC-20260917) share the same policy.
        services.AddHttpClient("mcp-upstream", c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(4);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(3);
            });
        // SPEC-20260924-gdrive-shared-link-connector: Drive API + public downloads.
        services.AddHttpClient<Ingestion.Connectors.GoogleDriveApiClient>(
                c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(3);
            });

        // SPEC-20260927-restapi-sqldatabase-connectors RF-001/RF-007: named
        // "restapi" client (30 s) + connector registry entries.
        services.AddHttpClient("restapi", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient("feed", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
    }

    private static void AddSourceConnectors(IServiceCollection services)
    {
        // SPEC-20260914-webpage-docfile-connectors: connector registry.
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.WebPageConnector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.DocumentFileConnector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.NotionConnector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.RestApiConnector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.SqlDatabaseConnector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.RssFeedConnector>();
        // SPEC-20260927-unstructured-document-parser-connector: external parsing
        // endpoint (Unstructured.io/Upstage-compatible) for PDF/Office/images.
        services.AddSingleton<Ingestion.Connectors.UnstructuredApiClient>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.UnstructuredDocumentConnector>();
        services.AddHttpClient("unstructured").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        // SPEC-20260927-git-repository-source-connector: read-only REST git
        // connector (github/gitlab/gitea).
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.GitRepositoryConnector>();
        services.AddHttpClient("git").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        // SPEC-20260927-audio-transcription-connector: external transcription
        // (AssemblyAI / OpenAI-whisper-compatible) — long-running uploads.
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.AudioTranscriptionConnector>();
        services.AddHttpClient("audio", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .SetHandlerLifetime(TimeSpan.FromMinutes(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddHttpMessageHandler(sp => Security.EgressPolicyHandler.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        // SPEC-20260927-youtube-transcript-connector: YoutubeExplode adapter + connector.
        services.AddSingleton<Ingestion.Connectors.IYouTubeClient, Ingestion.Connectors.YouTubeClientAdapter>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.YouTubeConnector>();
        // SPEC-20260924-cloud-storage-connectors: remote object stores staged locally.
        services.AddSingleton<Ingestion.Staging.IStagingStorageService, Ingestion.Staging.StagingStorageService>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.Cloud.AwsS3Connector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.Cloud.AzureFilesConnector>();
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.Cloud.OciStorageConnector>();
        // SPEC-20260924-gdrive-shared-link-connector.
        services.AddSingleton<Ingestion.Connectors.ISourceConnector, Ingestion.Connectors.GoogleDriveSharedConnector>();
    }

    private static void AddChatProviders(IServiceCollection services)
    {
        // SPEC-20260914-llm-answer-synthesis RF-001: optional chat client.
        // Provider=none → GetClient() returns null; consumers use GetService.
        services.AddOptions<Chat.ChatProviderOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(Chat.ChatProviderOptions.SectionName).Bind(options));
        services.AddHttpClient("chat", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(o =>
            {
                // LLM answers can legitimately take minutes on local providers.
                o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(3);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(8);
            });
        // SPEC-20260916-settings-chat-config RF-003: effective config comes from
        // the Settings store when a ChatSettings row exists, else env — the client
        // is resolved per scope (nullable) so /settings edits apply without restart.
        // Func<HttpClient> is an optional test seam for the connection probe.
        services.AddSingleton<Settings.IChatSettingsService>(sp => new Settings.ChatSettingsService(
            sp.GetRequiredService<IOptions<Chat.ChatProviderOptions>>(),
            sp.GetRequiredService<Settings.IIntegrationSecretStore>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<Settings.ChatSettingsService>>(),
            sp.GetService<Func<HttpClient>>()));
        services.AddSingleton<Settings.IApiKeyChatSettingsService>(sp => new Settings.ApiKeyChatSettingsService(
            sp.GetRequiredService<Settings.IChatSettingsService>(),
            sp.GetRequiredService<Settings.IIntegrationSecretStore>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<Settings.ApiKeyChatSettingsService>>()));
        // SPEC-20260927-tool-and-model-resilience-fallback: policy engine +
        // capability registry are singletons; the IChatClient decorator wraps
        // the resolved primary when Mode != disabled and fallbacks exist.
        services.AddOptions<Resilience.FallbackOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(Resilience.FallbackOptions.SectionName).Bind(options));
        services.AddSingleton<Resilience.IFallbackPolicyEngine, Resilience.FallbackPolicyEngine>();
        services.AddSingleton<Resilience.ToolCapabilityRegistry>();
        // SPEC-20260928-resilience-tool-fallback-wiring RF-004: runtime-editable
        // fallback settings (persisted row overrides Resilience:Fallback config).
        services.AddSingleton<Settings.IResilienceSettingsService, Settings.ResilienceSettingsService>();
        // SPEC-20260929-a2a-assistant-delegation RF-001/RF-002: low-cost assistant
        // provider (local OpenAI-compatible or remote A2A agent) routed to cheap
        // sub-tasks; snapshot invalidates on settings save — no restart.
        services.AddOptions<Assistant.AssistantOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(Assistant.AssistantOptions.SectionName).Bind(options));
        services.AddSingleton<Assistant.IAssistantChatClientProvider, Assistant.AssistantChatClientProvider>();
        services.AddSingleton<Settings.IAssistantSettingsService, Settings.AssistantSettingsService>();
        services.AddScoped<Microsoft.Extensions.AI.IChatClient>(sp =>
        {
            var http = sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()?.HttpContext;
            var keyIdValue = http?.User.FindFirst(Auth.ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
            Microsoft.Extensions.AI.IChatClient? client;
            string? primaryName;
            if (keyIdValue is not null && Guid.TryParse(keyIdValue, out var keyId))
            {
                var perKey = sp.GetRequiredService<Settings.IApiKeyChatSettingsService>();
                client = perKey.GetClient(keyId);
                primaryName = perKey.GetEffectiveOptions(keyId).Provider;
            }
            else
            {
                var chat = sp.GetRequiredService<Settings.IChatSettingsService>();
                client = chat.GetClient();
                primaryName = chat.GetEffectiveOptions().Provider;
            }
            if (client is null)
                return client!;
            var fb = sp.GetRequiredService<Settings.IResilienceSettingsService>().GetEffective();
            return Resilience.ResilientChatClient.Wrap(
                client, primaryName ?? "primary", fb.ChatFallbacks,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<Resilience.IFallbackPolicyEngine>(),
                sp.GetRequiredService<ILogger<Resilience.ResilientChatClient>>());
        });
        services.AddScoped<IAnswerService>(sp =>
        {
            var http = sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()?.HttpContext;
            var keyIdValue = http?.User.FindFirst(Auth.ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
            if (keyIdValue is not null && Guid.TryParse(keyIdValue, out var keyId))
            {
                return new AnswerService(
                    sp.GetService<Microsoft.Extensions.AI.IChatClient>(),
                    sp.GetRequiredService<Settings.IApiKeyChatSettingsService>().GetEffectiveOptions(keyId),
                    sp.GetRequiredService<Caching.L1L2Cache>(),
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<ILogger<AnswerService>>(),
                    sp.GetRequiredService<Evaluation.IRagEvaluationEnqueuer>());
            }
            return new AnswerService(
                sp.GetService<Microsoft.Extensions.AI.IChatClient>(),
                sp.GetRequiredService<Settings.IChatSettingsService>().GetEffectiveOptions(),
                sp.GetRequiredService<Caching.L1L2Cache>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AnswerService>>(),
                sp.GetRequiredService<Evaluation.IRagEvaluationEnqueuer>());
        });
    }

    private static void AddAgentServices(IServiceCollection services)
    {
        // SPEC-20260927-cryptographic-evidence-provenance-chain: append-only
        // receipt chain (HMAC key in the encrypted store, slot evidence:master).
        services.AddScoped<Audit.Evidence.IEvidenceChainService, Audit.Evidence.EvidenceChainService>();

        // SPEC-20260927-chain-ast-thread-compactor: compaction/repair of the
        // message projection sent to the LLM (transcript stays untouched).
        services.AddSingleton<McpEngine.Agents.ChainAst.IChainCompactor>(sp =>
            new McpEngine.Agents.ChainAst.ChainCompactor(
                sp.GetRequiredService<IOptions<Agent.AgentOptions>>().Value.ContextManagement));

        // SPEC-20260914-agent-chat-loop: model→tools→model loop over the live catalog.
        services.AddOptions<Agent.AgentOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(Agent.AgentOptions.SectionName).Bind(options));
        services.AddScoped<IAgentService>(sp => new AgentService(
            sp.GetService<Microsoft.Extensions.AI.IChatClient>(),
            sp,
            sp.GetRequiredService<IDynamicToolCatalog>(),
            sp.GetRequiredService<Data.KnowledgeHubDbContext>(),
            sp.GetRequiredService<IOptions<Agent.AgentOptions>>().Value,
            new AgentDiagnostics(
                sp.GetService<IMcpActivityFeed>(),
                sp.GetService<McpEngine.Agents.ChainAst.IChainCompactor>(),
                sp.GetService<Audit.Evidence.IEvidenceChainService>()),
            sp.GetRequiredService<ILogger<AgentService>>()));
        services.AddScoped<IApprovalService>(sp => new ApprovalService(
            sp.GetRequiredService<Data.KnowledgeHubDbContext>(),
            TimeSpan.FromMinutes(
                sp.GetRequiredService<IOptions<Agent.AgentOptions>>().Value.ApprovalTimeoutMinutes),
            sp.GetService<IMcpActivityFeed>()));
        services.AddScoped<IConversationService, ConversationService>();
    }

    private static void AddVectorStore(IServiceCollection services)
    {
        services.AddScoped<IVectorStore>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var cat = sp.GetRequiredService<CatalogDatabase>();
            var provider = cfg.GetValue<string>("VectorStore:Provider");
            if (string.IsNullOrWhiteSpace(provider))
                // SPEC-20260926-unified-database-provider RF-004: the vector
                // store follows the effective catalog provider unless an
                // explicit VectorStore:Provider override is set.
                provider = cat.IsPostgres ? "postgres" : "sqlite";
            else if (cat.IsPostgres != provider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("VectorStore")
                    .LogWarning(
                        "VectorStore:Provider='{VectorProvider}' diverges from the effective catalog provider " +
                        "('{CatalogProvider}') — mixed mode is an advanced configuration.",
                        provider, cat.Provider);

            if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                return new PostgresVectorStore(
                    cfg.GetValue<string>("VectorStore:ConnectionString")
                        ?? cat.PostgresConnectionString
                        ?? throw new InvalidOperationException("VectorStore:ConnectionString is required when VectorStore:Provider=postgres"),
                    cfg.GetValue("Embeddings:Dimensions", 384),
                    cfg.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>() ?? new PostgresOptions());
            // SPEC-20260917-sqlite-vec-search: opt-in native KNN via the
            // sqlite-vec vec0 extension; "sqlite" stays the default.
            if (provider.Equals("sqlite-vec", StringComparison.OrdinalIgnoreCase))
                return new SqliteVecVectorStore(
                    sp.GetRequiredService<KnowledgeHubDbContext>(),
                    cfg.GetValue("Embeddings:Dimensions", 384));
            return new SqliteVectorStore(sp.GetRequiredService<KnowledgeHubDbContext>());
        });
    }

    private static void AddRetrieval(IServiceCollection services)
    {
        // Extras were never registered — staging/vector cleanup on source
        // delete and the index-version bump were dead code in production.
        services.AddScoped(sp => new Services.KnowledgeSourceServiceExtras(
            sp.GetRequiredService<Ingestion.Staging.IStagingStorageService>(),
            sp.GetRequiredService<VectorStore.IVectorStore>(),
            sp.GetRequiredService<ILogger<Services.KnowledgeSourceService>>(),
            sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<Caching.ICacheInvalidationBus>()));
        services.AddScoped<IKnowledgeSourceService, KnowledgeSourceService>();
        services.AddScoped<Search.ILexicalSearchService, Search.LexicalSearchService>();
        services.AddScoped<ISearchService>(sp => new SearchService(
            sp.GetRequiredService<Data.KnowledgeHubDbContext>(),
            sp.GetRequiredService<Embeddings.IEmbeddingProvider>(),
            sp.GetRequiredService<Embeddings.IEmbeddingProviderResolver>(),
            sp.GetRequiredService<VectorStore.IVectorStore>(),
            sp.GetRequiredService<Search.ILexicalSearchService>(),
            sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<Search.IQueryRewriter>(),
            sp.GetRequiredService<Search.IQueryExpander>(),
            sp.GetRequiredService<Graph.GraphEntityLinker>(),
            sp.GetRequiredService<Search.IReranker>(),
            sp.GetRequiredService<Auth.ICallerScopeProvider>(),
            sp.GetRequiredService<Settings.IGraphSettingsService>(),
            sp.GetRequiredService<ILogger<SearchService>>()));
        // SPEC-20260923-source-authorization RF-002: per-request caller scope.
        services.AddScoped<Auth.ICallerScopeProvider, Auth.CallerScopeProvider>();
        // SPEC-20260923-retrieval-quality: opt-in query rewriting + reranker.
        services.AddScoped<Search.IQueryRewriter>(sp => new Search.LlmQueryRewriter(
            sp, sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<Search.LlmQueryRewriter>>()));
        // SPEC-20260924-query-expansion-hyde: multi-query + HyDE (opt-in).
        services.AddScoped<Search.IQueryExpander>(sp => new Search.LlmQueryExpander(
            sp, sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<Search.LlmQueryExpander>>()));
        services.AddScoped<Search.IReranker>(sp =>
            sp.GetRequiredService<IConfiguration>().GetValue("Search:Rerank:Enabled", false)
                && sp.GetService<Microsoft.Extensions.AI.IChatClient>() is { } chat
                ? new Search.LlmReranker(chat, sp.GetRequiredService<ILogger<Search.LlmReranker>>())
                : Search.NoOpReranker.Instance);
        // SPEC-20260924-corrective-rag RF-001: retrieval grading + corrective loop.
        services.AddScoped<Search.IRetrievalGrader>(sp =>
            string.Equals(
                sp.GetRequiredService<IConfiguration>().GetValue("Search:Grading:Mode", "off"),
                "llm", StringComparison.OrdinalIgnoreCase)
                ? (Search.IRetrievalGrader)new Search.LlmRetrievalGrader(
                    sp, sp.GetRequiredService<ILogger<Search.LlmRetrievalGrader>>())
                : new Search.HeuristicRetrievalGrader(sp.GetRequiredService<IConfiguration>()));
        services.AddScoped<CorrectiveRetrievalService>();
    }

    private static void AddGraph(IServiceCollection services)
    {
        // SPEC-20260923-graph-settings-ui: runtime-editable Graph:* overrides.
        services.AddSingleton<Settings.IGraphSettingsService, Settings.GraphSettingsService>();

        // SPEC-20260923-graphrag: adjacency-table store + LLM extractor.
        services.AddScoped<Graph.IKnowledgeGraphStore, Graph.SqliteKnowledgeGraphStore>();
        // SPEC-20260924-graph-expanded-retrieval RF-001: lexical entity linker.
        services.AddScoped<Graph.GraphEntityLinker>();
        // SPEC-20260927-temporal-episodic-knowledge-graph: temporal retriever +
        // episode registry for ingestion runs and agent sessions.
        services.AddScoped<Graph.TemporalGraphRetriever>();
        services.AddScoped<Graph.GraphEpisodeService>();
        services.AddScoped<Graph.EntityExtractor>(sp => new Graph.EntityExtractor(
            sp.GetService<Microsoft.Extensions.AI.IChatClient>(),
            sp.GetRequiredService<Settings.IGraphSettingsService>()));
        // SPEC-20260923-prompt-injection-guard: deterministic heuristic scanner.
        services.AddSingleton<Security.IContentSanitizer, Security.ContentSanitizer>();
    }

    private static void AddIngestion(IServiceCollection services)
    {
        services.AddSingleton(sp => new Ingestion.IngestionServiceDeps(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Embeddings.IEmbeddingProvider>(),
            sp.GetRequiredService<IEnumerable<Ingestion.Connectors.ISourceConnector>>(),
            sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<Security.IContentSanitizer>(),
            sp.GetRequiredService<Settings.IGraphSettingsService>(),
            sp.GetRequiredService<Settings.IEmbeddingSettingsService>(),
            sp.GetService<Microsoft.Extensions.Caching.Hybrid.HybridCache>()));
        services.AddSingleton<IngestionService>();
        services.AddSingleton<IIngestionService>(sp => sp.GetRequiredService<IngestionService>());
        // SPEC-20260924-async-ingestion-queue RF-001/RF-002: bounded channel +
        // sequential worker (SQLite write lock keeps MaxParallelJobs at 1).
        services.AddSingleton<Ingestion.IIngestionQueue, Ingestion.IngestionQueue>();
        services.AddSingleton<Ingestion.IIngestionProgressFeed, Ingestion.IngestionProgressFeed>();
        services.AddHostedService<Ingestion.IngestionWorker>();
        services.AddHostedService<VaultWatcherService>();
        // SPEC-20260924-hosted-services-and-serilog-logging RF-002/RF-003.
        services.AddHostedService<ScheduledSyncBackgroundService>();
        services.AddHostedService<MaintenanceBackgroundService>();
    }

    private static void AddToolCatalog(IServiceCollection services)
    {
        // SPEC-04: dynamic MCP tool catalog + handlers + change notifier.
        services.AddSingleton<IToolProvider, KnowledgeHub.Server.Mcp.ToolProviders.KnowledgeToolsProvider>();
        services.AddSingleton<IToolProvider, KnowledgeHub.Server.Mcp.ToolProviders.SourceQueryToolsProvider>();
        services.AddSingleton<IToolProvider, KnowledgeHub.Server.Mcp.ToolProviders.ObsidianToolsProvider>();
        services.AddSingleton<IDynamicToolCatalog, DynamicToolCatalog>();
        services.AddSingleton<IToolCatalogChangeNotifier, ToolCatalogChangeNotifier>();
    }

    private static void AddSecrets(IServiceCollection services, IConfiguration configuration)
    {
        // SPEC-20260916-firecrawl-mcp-proxy RF-004: encrypted-at-rest upstream
        // credentials. DP key ring lives next to the DB so backup.sh can ship it.
        services.AddDataProtection()
            // Pinned app name: the default discriminator is the content root,
            // which differs between container (/app) and local runs — pinning
            // keeps stored secrets decryptable across deployments.
            .SetApplicationName("KnowledgeHub")
            .PersistKeysToFileSystem(new DirectoryInfo(
                Path.Join(
                    Path.GetDirectoryName(DatabasePath.Resolve(configuration))!,
                    "dataprotection-keys")));
        services.AddSingleton<Settings.IIntegrationSecretStore, Settings.IntegrationSecretStore>();
        services.AddSingleton<Settings.IIntegrationStateService, Settings.IntegrationStateService>();
    }

    private static void AddUpstreamTools(IServiceCollection services)
    {
        // SPEC-07: DeepWiki proxy tools (ask_question / read_wiki_structure / read_wiki_contents).
        services.AddOptions<KnowledgeHub.Server.Mcp.Upstream.DeepWikiOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(KnowledgeHub.Server.Mcp.Upstream.DeepWikiOptions.SectionName).Bind(options));
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.DeepWikiUpstreamClient>();
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.DeepWikiToolsProvider>();
        services.AddSingleton<IToolProvider>(sp =>
            sp.GetRequiredService<KnowledgeHub.Server.Mcp.Upstream.DeepWikiToolsProvider>());

        // SPEC-20260916-firecrawl-mcp-proxy: Firecrawl proxy tools (firecrawl_*).
        // Concrete-type registration lets SettingsEndpoints reset the client and
        // invalidate the provider's tools cache on key save/remove.
        services.AddOptions<KnowledgeHub.Server.Mcp.Upstream.FirecrawlOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(KnowledgeHub.Server.Mcp.Upstream.FirecrawlOptions.SectionName).Bind(options));
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.FirecrawlUpstreamClient>();
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.FirecrawlToolsProvider>();
        services.AddSingleton<IToolProvider>(sp =>
            sp.GetRequiredService<KnowledgeHub.Server.Mcp.Upstream.FirecrawlToolsProvider>());

        // SPEC-20260916-tavily-mcp-proxy: Tavily proxy tools (tavily_*).
        services.AddOptions<KnowledgeHub.Server.Mcp.Upstream.TavilyOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(KnowledgeHub.Server.Mcp.Upstream.TavilyOptions.SectionName).Bind(options));
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.TavilyUpstreamClient>();
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.TavilyToolsProvider>();
        services.AddSingleton<IToolProvider>(sp =>
            sp.GetRequiredService<KnowledgeHub.Server.Mcp.Upstream.TavilyToolsProvider>());

        // SPEC-20260922-context7-mcp-proxy: Context7 proxy tools
        // (resolve-library-id / query-docs).
        services.AddOptions<KnowledgeHub.Server.Mcp.Upstream.Context7Options>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(KnowledgeHub.Server.Mcp.Upstream.Context7Options.SectionName).Bind(options));
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.Context7UpstreamClient>();
        services.AddSingleton<KnowledgeHub.Server.Mcp.Upstream.Context7ToolsProvider>();
        services.AddSingleton<IToolProvider>(sp =>
            sp.GetRequiredService<KnowledgeHub.Server.Mcp.Upstream.Context7ToolsProvider>());

        // SPEC-20260917-mcp-proxy-source-type: generic upstream MCP proxies
        // driven by McpProxy sources (tools re-exposed with slug prefix).
        services.AddSingleton<IToolProvider, KnowledgeHub.Server.Mcp.Upstream.McpProxyToolsProvider>();
        services.AddSingleton<KnowledgeHub.Server.Mcp.ToolProviders.SettingsToolsProvider>();
        services.AddSingleton<IToolProvider>(sp =>
            sp.GetRequiredService<KnowledgeHub.Server.Mcp.ToolProviders.SettingsToolsProvider>());
        services.AddSingleton<IToolProvider>(sp =>
            new KnowledgeHub.Server.Mcp.ToolProviders.GraphToolsProvider(
                sp.GetRequiredService<Settings.IGraphSettingsService>()));
        // SPEC-20260927-temporal-episodic-knowledge-graph RF-005: temporal,
        // recent-window, diverse and episodic graph search tools.
        services.AddSingleton<IToolProvider>(sp =>
            new KnowledgeHub.Server.Mcp.ToolProviders.TemporalGraphToolsProvider(
                sp.GetRequiredService<Settings.IGraphSettingsService>()));
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        // SPEC-20260916-performance-memory-cache RF-005: IDistributedCache —
        // memory by default (zero-infra), Redis opt-in for shared/persistent
        // entries. Secrets never go through this store.
        services.AddOptions<Configuration.CacheOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(Configuration.CacheOptions.SectionName).Bind(options));
        services.AddMemoryCache();

        // SPEC-20260925-cache-region-ttl-policies: resolver for region-prefix
        // TTLs; registered for BOTH providers — call sites pass null and the
        // policy decides.
        services.AddSingleton<Caching.CacheTtlPolicy>();

        if (configuration.GetValue($"{Configuration.CacheOptions.SectionName}:Provider", "memory")
                .Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            var redisConnection = configuration
                .GetValue<string>($"{Configuration.CacheOptions.SectionName}:Redis:ConnectionString")
                ?? throw new InvalidOperationException(
                    "Cache:Redis:ConnectionString is required when Cache:Provider=redis");
            // SPEC-20260925-redis-health-and-scan-stats RF-001: multiplexer as a
            // named singleton — reused by the health check and server-side stats.
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
            {
                var parsed = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
                parsed.AbortOnConnectFail = false;
                parsed.ConnectTimeout = 3000;
                // SPEC-20260926-redis-stats-admin-and-connflag RF-001: INFO/SCAN
                // server stats in CacheManagerService are admin commands —
                // without this they throw "unless admin mode is enabled".
                parsed.AllowAdmin = true;
                return StackExchange.Redis.ConnectionMultiplexer.Connect(parsed);
            });

            // SPEC-20260925-distributed-invalidation-pubsub RF-001: Redis pub/sub
            // bus + the subscriber that evicts local L1 entries on remote events.
            services.AddSingleton<Caching.ICacheInvalidationBus, Caching.RedisInvalidationBus>();
            services.AddHostedService(sp => new Caching.InvalidationSubscriber(
                sp.GetRequiredService<Caching.ICacheInvalidationBus>(),
                sp.GetRequiredService<Caching.L1L2Cache>(),
                sp.GetRequiredService<ILogger<Caching.InvalidationSubscriber>>(),
                sp.GetService<Caching.ICacheManagerService>(),
                sp.GetService<Settings.IEmbeddingSettingsService>(),
                sp.GetService<Microsoft.Extensions.Caching.Hybrid.HybridCache>()));

            // IDistributedCache = the RAW L2 backend (Redis). The in-process
            // tiers layer on top: L1L2Cache for the SafeCache-era paths and
            // HybridCache for EndpointCache. (AddSingleton factory instead of
            // AddStackExchangeRedisCache so the multiplexer options apply.)
            services.AddSingleton<IDistributedCache>(_ =>
            {
                var redisOpts = new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions
                {
                    Configuration = redisConnection
                };
                try
                {
                    var parsed = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
                    parsed.AbortOnConnectFail = false;
                    parsed.ConnectTimeout = 3000;
                    parsed.AllowAdmin = true;
                    redisOpts.ConfigurationOptions = parsed;
                }
                catch
                {
                    // If parsing fails fall back to connection string only
                }

                return new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache(
                    Microsoft.Extensions.Options.Options.Create(redisOpts));
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<Caching.ICacheInvalidationBus, Caching.NoopInvalidationBus>();
        }

        var l1Cap = configuration.GetValue(
            $"{Configuration.CacheOptions.SectionName}:L1Enabled", true)
            ? TimeSpan.FromMinutes(configuration.GetValue(
                $"{Configuration.CacheOptions.SectionName}:L1MaxTtlMinutes", 5))
            : TimeSpan.Zero;

        // SPEC-20260925-hybrid-cache-l1l2 RF-001: in-process L1 in front of
        // the L2 backend for the SafeCache-era paths — registered as the
        // concrete type now that IDistributedCache is the raw backend; hits
        // no longer pay a network RTT and per-key locks collapse stampedes.
        services.AddSingleton(sp => new Caching.L1L2Cache(
            sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<IDistributedCache>(), l1Cap,
            sp.GetService<ILogger<Caching.L1L2Cache>>()));

        // Microsoft.Extensions.Caching.Hybrid: endpoint-level response cache
        // (settings describes, list payloads, MCP/A2A metadata) — L1 serves
        // live objects with zero serialization, the registered
        // IDistributedCache is its L2, and RemoveByTagAsync drives grouped
        // invalidation instead of hand-rolled version tokens.
        services.AddHybridCache();

        services.AddSingleton<Caching.ICacheManagerService>(sp => new Caching.CacheManagerService(
            sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<IOptions<Configuration.CacheOptions>>(),
            sp.GetRequiredService<ILogger<Caching.CacheManagerService>>(),
            sp.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
            sp.GetService<Caching.ICacheInvalidationBus>()));
        services.AddSingleton<Caching.IToolCacheService>(sp => new Caching.ToolCacheService(
            sp.GetRequiredService<Caching.L1L2Cache>(),
            sp.GetRequiredService<IOptions<Configuration.CacheOptions>>(),
            sp.GetRequiredService<ILogger<Caching.ToolCacheService>>()));
    }

    private static void AddMcpServer(IServiceCollection services)
    {
        services.AddOptions<McpServerOptions>().Configure(options =>
        {
            options.Handlers.ListToolsHandler = async (ctx, ct) =>
            {
                var catalog = ctx.Services!.GetRequiredService<IDynamicToolCatalog>();
                var tools = await catalog.GetToolsAsync(ctx.Services!, ct);
                return new ListToolsResult
                {
                    // RF-005 (SPEC-20260926-mcp-sdk-alignment): the catalog is
                    // per-credential — private scope, short TTL so scope or
                    // integration changes propagate quickly.
                    CacheScope = CacheScope.Private,
                    TimeToLive = TimeSpan.FromMinutes(5),
                    Tools = tools.Select(t => new Tool
                    {
                        Name = t.Name,
                        Title = t.Title,
                        Description = t.Description,
                        InputSchema = JsonSerializer.SerializeToElement(t.InputSchema),
                        OutputSchema = t.OutputSchema is { } os
                            ? JsonSerializer.SerializeToElement(os) : null,
                        Annotations = new ToolAnnotations
                        {
                            Title = t.Title,
                            ReadOnlyHint = t.ReadOnly,
                            DestructiveHint = t.DestructiveHint,
                            IdempotentHint = t.IdempotentHint,
                            OpenWorldHint = t.OpenWorldHint
                        }
                    }).ToList()
                };
            };

            options.Handlers.CallToolHandler = async (ctx, ct) => await CallToolAsync(ctx, ct);

            options.Handlers.ListResourcesHandler = async (ctx, ct) =>
                await KnowledgeResourceProvider.ListAsync(ctx.Services!, ct);

            options.Handlers.ReadResourceHandler = async (ctx, ct) =>
                await KnowledgeResourceProvider.ReadAsync(ctx.Params?.Uri ?? "", ctx.Services!, ct);
        });
    }

    private static void AddTelemetry(IServiceCollection services, IConfiguration configuration)
    {
        // SPEC-20260923-observability-metrics RF-003: opt-in exporters. With no
        // Telemetry:* config the Meter/ActivitySource stay no-op listeners —
        // zero exporter overhead and zero behavioral change.
        services.AddSingleton<IMcpRequestMetrics, Telemetry.KnowledgeHubMetrics>();
        // SPEC-20260925-runtime-log-level: LogLevelControl + LoggingLevelSwitch
        // are registered in Program.cs (the switch must exist before Serilog
        // config binds to it).
        var telemetry = Telemetry.TelemetryOptions.FromConfiguration(configuration);
        if (!string.IsNullOrEmpty(telemetry.OtlpEndpoint) || telemetry.Prometheus)
        {
            var otel = services.AddOpenTelemetry()
                .WithMetrics(m => m
                    .AddMeter(Telemetry.KnowledgeHubMetrics.MeterName)
                    .AddMeter(Evaluation.RagEvaluationMetrics.MeterName)
                    .AddAspNetCoreInstrumentation())
                .WithTracing(t => t
                    .AddSource(Telemetry.KnowledgeHubMetrics.MeterName)
                    .AddAspNetCoreInstrumentation());
            if (!string.IsNullOrEmpty(telemetry.OtlpEndpoint))
            {
                var endpoint = new Uri(telemetry.OtlpEndpoint);
                otel.WithMetrics(m => m.AddOtlpExporter(o => o.Endpoint = endpoint))
                    .WithTracing(t => t.AddOtlpExporter(o => o.Endpoint = endpoint));
            }
            if (telemetry.Prometheus)
                otel.WithMetrics(m => m.AddPrometheusExporter());
        }
    }

    /// <summary>MCP CallTool pipeline: scope gate → MRTR approval → rate limit →
    /// tool cache → handler with duration metric.</summary>
    private static async System.Threading.Tasks.Task<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var name = ctx.Params?.Name;
        var (tool, denied) = await ResolveMcpToolAsync(ctx, name, ct);
        if (denied is not null)
            return denied;

        if (await WriteGateDeniedAsync(ctx, tool!, name, ct) is { } writeDenied)
            return writeDenied;

        // RF-003 (SPEC-20260926-mcp-sdk-alignment): MRTR — a retry
        // carrying requestState+inputResponses resolves the pending
        // approval (and executes the stored call on accept). A gated
        // write tool under an elicitation-capable client is answered
        // with resultType:"input_required" instead of running blind.
        if (Mcp.MrtrApproval.IsRetry(ctx))
            return await Mcp.MrtrApproval.ResumeAsync(ctx, tool!, ct);

        if (Mcp.MrtrApproval.RequiresApproval(ctx.Services!, tool!)
            && Mcp.MrtrApproval.ClientSupportsElicitation(ctx))
            throw await Mcp.MrtrApproval.CreateAsync(ctx, tool!, ct);

        if (RateLimitedResult(ctx, name) is { } limited)
            return limited;

        return await InvokeMcpToolAsync(ctx, tool!, name, ct);
    }

    /// <summary>Catalog resolution: a tool hidden by the key's scope gets a
    /// friendly isError + audit row; genuinely unknown names stay
    /// MethodNotFound (SPEC-20260923-source-authorization RF-004).</summary>
    private static async System.Threading.Tasks.Task<(CatalogTool? Tool, CallToolResult? Denied)>
        ResolveMcpToolAsync(RequestContext<CallToolRequestParams> ctx, string? name, CancellationToken ct)
    {
        var catalog = ctx.Services!.GetRequiredService<IDynamicToolCatalog>();
        var tool = (await catalog.GetToolsAsync(ctx.Services!, ct))
            .FirstOrDefault(t => t.Name == name);
        if (tool is not null)
            return (tool, null);

        var exists = (await catalog.GetUnfilteredToolsAsync(ctx.Services!, ct))
            .Any(t => t.Name == name);
        if (!exists)
            throw new McpProtocolException($"unknown tool '{name}'", McpErrorCode.MethodNotFound);

        await Auth.ScopeAudit.RecordToolDeniedAsync(ctx.Services!, name!, ct);
        return (null, new CallToolResult
        {
            IsError = true,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock
            {
                Text = $"tool '{name}' is not available for this credential"
            }]
        });
    }

    /// <summary>Per-key write gate: read-only credentials get an informative
    /// isError instead of the write executing — the tool stays visible in
    /// tools/list so clients can discover it.</summary>
    private static async System.Threading.Tasks.Task<CallToolResult?> WriteGateDeniedAsync(
        RequestContext<CallToolRequestParams> ctx, CatalogTool tool, string? name, CancellationToken ct)
    {
        var callScope = ctx.Services!.GetService<Auth.ICallerScopeProvider>() is { } scopeProvider
            ? await scopeProvider.GetAsync(ct)
            : Auth.CallerScope.Unrestricted;
        if (tool.ReadOnly || callScope.AllowWrite)
            return null;

        await Auth.ScopeAudit.RecordToolDeniedAsync(ctx.Services!, name!, ct);
        return new CallToolResult
        {
            IsError = true,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock
            {
                Text = $"tool '{name}' requires write access — this credential is read-only"
            }]
        };
    }

    /// <summary>SPEC-20260923-rate-limiting RF-003: LLM-spending / write tools
    /// are charged per caller — over-limit yields a friendly isError result
    /// (JSON-RPC has no 429).</summary>
    private static CallToolResult? RateLimitedResult(
        RequestContext<CallToolRequestParams> ctx, string? name)
    {
        var limiter = ctx.Services!.GetRequiredService<RateLimiting.McpToolRateLimiter>();
        var http = ctx.Services!.GetService<IHttpContextAccessor>()?.HttpContext;
        if (limiter.TryAcquire(name!, http, out var retryAfter))
            return null;
        return new CallToolResult
        {
            IsError = true,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock
            {
                Text = $"rate limited — retry in {retryAfter}s"
            }]
        };
    }

    /// <summary>Tool cache lookup + handler invocation with duration metric;
    /// cacheable results are stored for subsequent calls.</summary>
    private static async System.Threading.Tasks.Task<CallToolResult> InvokeMcpToolAsync(
        RequestContext<CallToolRequestParams> ctx, CatalogTool tool, string? name, CancellationToken ct)
    {
        var toolCache = ctx.Services!.GetService<Caching.IToolCacheService>();
        if (toolCache is not null && toolCache.IsCacheable(name!, tool.ReadOnly))
        {
            var cached = await toolCache.GetCachedResultAsync(name!, ctx.Params?.Arguments, ct);
            if (cached is not null)
                return cached;
        }

        var toolSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await tool.Handler(
                new KnowledgeHub.Server.Mcp.ToolCallContext
                {
                    Services = ctx.Services!,
                    Arguments = ctx.Params?.Arguments
                }, ct);

            if (toolCache is not null && toolCache.IsCacheable(name!, tool.ReadOnly))
            {
                await toolCache.SetCachedResultAsync(name!, ctx.Params?.Arguments, result, ct);
            }

            return result;
        }
        finally
        {
            Telemetry.KnowledgeHubMetrics.ToolDuration.Record(toolSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", name));
        }
    }

}
