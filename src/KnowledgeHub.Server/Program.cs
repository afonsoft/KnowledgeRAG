using A2A.AspNetCore;
using KnowledgeHub.McpEngine;
using KnowledgeHub.Server.A2A;
using KnowledgeHub.Server;
using KnowledgeHub.Server.Mcp;
using ModelContextProtocol.Extensions.Tasks;
using KnowledgeHub.Server.Api;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Configuration;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Health;
using KnowledgeHub.Server.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Serilog;

const string GeneralPolicy = "general";
const string ReadyTag = "ready";

var builder = WebApplication.CreateBuilder(args);

// SPEC-20260924-hosted-services-and-serilog-logging RF-001: Serilog host
// logger — console + rolling file under logs/, enriched with LogContext props.
// SPEC-20260925-runtime-log-level RF-001: the switch lives outside DI-build so
// the Serilog config can bind to it before the provider exists.
var logLevelControl = new KnowledgeHub.Server.Telemetry.LogLevelControl();
builder.Services.AddSingleton(logLevelControl);
builder.Services.AddSingleton<Serilog.Core.LoggingLevelSwitch>(logLevelControl.Switch);

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .MinimumLevel.ControlledBy(logLevelControl.Switch)
        .Enrich.FromLogContext()
        // SPEC-20260925-log-sinks-and-redaction RF-002: secrets never reach a
        // sink — redact sensitive property names and token-shaped values.
        .Enrich.With(new KnowledgeHub.Server.Telemetry.SensitiveDataEnricher());

    // RF-004: optional OTLP log sink — same collector as traces/metrics.
    var otlp = ctx.Configuration.GetValue<string>("Telemetry:Otlp:Endpoint");
    if (!string.IsNullOrEmpty(otlp))
        cfg.WriteTo.OpenTelemetry(o => o.Endpoint = otlp);
});

// SPEC-20260914-config-validation: fail fast on invalid config before any work.
ConfigurationValidator.Validate(builder.Configuration);

// SPEC-20260914-graceful-shutdown: bounded drain window for in-flight requests.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout =
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Host:ShutdownTimeoutSeconds", 30)));

// SPEC-20260914-error-handling: RFC 7807 ProblemDetails for unhandled errors.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// SPEC-20260914-health-checks: /health/live + /health/ready.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
    .AddCheck<EmbeddingHealthCheck>("embeddings", tags: [ReadyTag])
    .AddCheck<IngestionHealthCheck>("ingestion", tags: [ReadyTag])
    // SPEC-20260925-vectorstore-metrics RF-003: store down → Degraded (search
    // keeps working via FTS-only) — never Unhealthy.
    .AddCheck<VectorStoreHealthCheck>("vectorstore", tags: [ReadyTag]);

// SPEC-20260925-redis-health-and-scan-stats RF-001: Redis is degraded-not-fatal
// (cache is fail-soft) — the check reports Degraded so ready stays 200.
if (builder.Configuration.GetValue("Cache:Provider", "memory")
        .Equals("redis", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHealthChecks()
        .Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
            "redis",
            sp => new KnowledgeHub.Server.Health.RedisHealthCheck(
                sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()),
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            tags: [ReadyTag],
            timeout: TimeSpan.FromSeconds(2)));
}

builder.Services.AddKnowledgeHubServer(builder.Configuration);
// SPEC-20260926-mcp-sdk-alignment RF-004: MCP Tasks extension — durable EF
// store; the selector marks long-running tools (agent_chat, billable upstream
// crawls/research) as task-required so capable clients get a taskId instead of
// blocking. Clients without the io.modelcontextprotocol/tasks extension keep
// the synchronous behavior.
builder.Services.AddKnowledgeHubMcp(builder.Configuration)
    .WithTasks(
        new EfMcpTaskStore(
            CatalogDatabase.Resolve(builder.Configuration), builder.Configuration),
        o => o.ExecutionModeSelector = ctx =>
            ctx.Params?.Name is "agent_chat" or "firecrawl_crawl" or "tavily_crawl" or "tavily_research"
                && MrtrApproval.ClientDeclaredExtension(ctx, TasksProtocol.ExtensionId)
                ? McpTaskExecutionMode.Required
                : McpTaskExecutionMode.Synchronous);
builder.Services.AddSignalR();

// SPEC-20260923-rate-limiting: partitioned policies — llm (sliding),
// sync (fixed), general (fixed). Partition precedence api-key → user → ip;
// unauthenticated callers get the stricter anon bucket on llm.
// NOTE: options bind lazily via DI — builder.Configuration at this point does
// NOT include test-host overrides (added during builder.Build()).
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
        .GetSection(KnowledgeHub.Server.RateLimiting.RateLimitOptions.SectionName)
        .Get<KnowledgeHub.Server.RateLimiting.RateLimitOptions>()
        ?? new KnowledgeHub.Server.RateLimiting.RateLimitOptions());
builder.Services.AddSingleton<KnowledgeHub.Server.RateLimiting.IApiKeyRateLimitResolver,
    KnowledgeHub.Server.RateLimiting.ApiKeyRateLimitResolver>();
builder.Services.AddSingleton<KnowledgeHub.Server.RateLimiting.McpToolRateLimiter>(sp =>
    new KnowledgeHub.Server.RateLimiting.McpToolRateLimiter(
        sp.GetRequiredService<KnowledgeHub.Server.RateLimiting.RateLimitOptions>(),
        sp.GetRequiredService<KnowledgeHub.Server.RateLimiting.IApiKeyRateLimitResolver>(),
        sp.GetRequiredService<ILogger<KnowledgeHub.Server.RateLimiting.McpToolRateLimiter>>()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (ctx, ct) =>
    {
        var response = ctx.HttpContext.Response;
        // Windowed limiters don't always attach Retry-After metadata — fall back
        // to the smallest configured window (most rejections are the llm policy).
        var opts = ctx.HttpContext.RequestServices
            .GetRequiredService<KnowledgeHub.Server.RateLimiting.RateLimitOptions>();
        var retry = ctx.Lease.TryGetMetadata(
            System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter)
            ? retryAfter
            : TimeSpan.FromSeconds(Math.Min(opts.LlmWindowSeconds, opts.GeneralWindowSeconds));
        response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString();
        response.ContentType = "application/problem+json";
        await response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.30",
            title = "rate_limited",
            status = 429,
            detail = $"retry in {response.Headers.RetryAfter.FirstOrDefault() ?? "60"}s"
        }, ct);
    };
    options.AddPolicy("llm", http =>
        RateLimiting(http, http.RequestServices.GetRequiredService<KnowledgeHub.Server.RateLimiting.RateLimitOptions>(), llm: true));
    options.AddPolicy("sync", http =>
        RateLimiting(http, http.RequestServices.GetRequiredService<KnowledgeHub.Server.RateLimiting.RateLimitOptions>(), llm: false, sync: true));
    options.AddPolicy(GeneralPolicy, http =>
        RateLimiting(http, http.RequestServices.GetRequiredService<KnowledgeHub.Server.RateLimiting.RateLimitOptions>(), llm: false));
});

static System.Threading.RateLimiting.RateLimitPartition<string> RateLimiting(
    HttpContext http, KnowledgeHub.Server.RateLimiting.RateLimitOptions o, bool llm, bool sync = false)
{
    var (key, _, _) = KnowledgeHub.Server.RateLimiting.CallerPartitioner.Resolve(http, o.TrustForwardedHeaders);
    // SPEC-20260923-per-key-rate-limits RF-003: a key: partition may carry a
    // per-key override — each field mixes with the global value.
    KnowledgeHub.Server.RateLimiting.ApiKeyRateLimitOverride? ov = null;
    if (key.StartsWith("key:", StringComparison.Ordinal)
        && Guid.TryParse(key.AsSpan(4), out var keyId))
        http.RequestServices
            .GetRequiredService<KnowledgeHub.Server.RateLimiting.IApiKeyRateLimitResolver>()
            .TryGetOverride(keyId, out ov);
    // RF-005: fingerprint on the partition key — editing/clearing an override
    // yields a fresh limiter (partitions never rebuild their options).
    if (ov is not null)
        key = $"{key}:{ov.LlmPermits}/{ov.LlmWindowSeconds}/{ov.SyncPermits}/{ov.SyncWindowSeconds}";
    if (llm)
    {
        var permit = key.StartsWith(KnowledgeHub.Server.RateLimiting.CallerPartitioner.AnonymousPrefix, StringComparison.Ordinal)
            ? o.AnonymousLlmPermitLimit : o.LlmPermitLimit;
        return System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
            new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
            {
                PermitLimit = ov?.LlmPermits ?? permit,
                Window = TimeSpan.FromSeconds(ov?.LlmWindowSeconds ?? o.LlmWindowSeconds),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }
    return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(key, _ =>
        new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = sync ? (ov?.SyncPermits ?? o.SyncPermitLimit) : o.GeneralPermitLimit,
            Window = TimeSpan.FromSeconds(sync ? (ov?.SyncWindowSeconds ?? o.SyncWindowSeconds) : o.GeneralWindowSeconds),
            QueueLimit = 0
        });
}
builder.Services.AddHostedService<McpActivityBroadcastService>();
builder.Services.AddHostedService<IngestionProgressBroadcastService>();

// SPEC-20260929-a2a-server-interop RF-001: KnowledgeHub as an A2A v1.0 agent.
// Card URL uses A2A:BaseUrl when set (behind proxies); the well-known endpoint
// rebuilds the card per-request with the actual scheme/host.
const string A2aFallbackBaseUrl = "http://localhost:5000/"; // NOSONAR — dev fallback; prod sets A2A:BaseUrl

// SPEC-20261001-a2a-task-durability RF-001/RF-003: durable EF task store +
// push-config CRUD replace the SDK's InMemoryTaskStore/base A2AServer —
// AddA2AAgent uses TryAdd*, so registering first wins.
builder.Services.AddSingleton<KnowledgeHub.Server.A2A.EfA2aTaskStore>();
builder.Services.AddSingleton<global::A2A.ITaskStore>(
    sp => sp.GetRequiredService<KnowledgeHub.Server.A2A.EfA2aTaskStore>());
builder.Services.AddSingleton<KnowledgeHub.Server.A2A.IA2aPushNotifier,
    KnowledgeHub.Server.A2A.A2aPushNotifier>();
builder.Services.AddSingleton<global::A2A.IA2ARequestHandler>(sp =>
    new KnowledgeHub.Server.A2A.KnowledgeHubA2AServer(
        sp.GetRequiredService<global::A2A.IAgentHandler>(),
        sp.GetRequiredService<KnowledgeHub.Server.A2A.EfA2aTaskStore>(),
        sp.GetRequiredService<global::A2A.ChannelEventNotifier>(),
        sp.GetRequiredService<ILogger<global::A2A.A2AServer>>(),
        sp.GetRequiredService<global::A2A.A2AServerOptions>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<KnowledgeHub.Server.A2A.IA2aPushNotifier>(),
        sp.GetRequiredService<ILogger<KnowledgeHub.Server.A2A.KnowledgeHubA2AServer>>()));
// RF-004: ambient write provenance (mcp|a2a channel, key id, agent name).
builder.Services.AddScoped<KnowledgeHub.Server.Mcp.WriteOriginContext>();

builder.Services.AddA2AAgent<KnowledgeHub.Server.A2A.KnowledgeHubA2AAgent>(
    KnowledgeHub.Server.A2A.A2AEndpointExtensions.BuildAgentCard(
        new Uri(builder.Configuration["A2A:BaseUrl"] ?? A2aFallbackBaseUrl),
        builder.Configuration.GetValue(
            KnowledgeHub.Server.A2A.KnowledgeHubA2AServer.EnabledConfigKey, true)));

// SPEC-20260914-auth-login: cookie session (browser SPA) + aft_* API keys
// (non-browser MCP/API/hub clients). Secure=SameAsRequest keeps dev/test over
// plain http working while production (https) always gets Secure cookies.
builder.Services.AddOptions<AuthOptions>()
    .Configure<IConfiguration>((options, cfg) =>
        cfg.GetSection(AuthOptions.SectionName).Bind(options));
builder.Services.AddSingleton<PasswordService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(
            builder.Configuration.GetValue("Auth:SessionHours", 12));
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(AuthPolicies.Authenticated, p => p
        .AddAuthenticationSchemes(AuthPolicies.AnyScheme)
        .RequireAuthenticatedUser());
    o.AddPolicy(AuthPolicies.Operational, p => p
        .AddAuthenticationSchemes(AuthPolicies.AnyScheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new PasswordChangedRequirement()));
    o.AddPolicy(AuthPolicies.CookieSession, p => p
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new PasswordChangedRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, PasswordChangedHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, PasswordGateResultHandler>();

var app = builder.Build();

// SPEC-20260926-cache-coherence-and-ttl RF-001: resolve the TTL-policy singleton
// eagerly — SafeCache reaches it through the static Current property; lazy DI
// would leave it null and silently apply the 10-min default to every region.
_ = app.Services.GetRequiredService<KnowledgeHub.Server.Caching.CacheTtlPolicy>();

// SPEC-20260916-redis-exposure-risk RF-002: non-fatal config warnings (e.g.
// Redis without auth) — surfaced once at startup, never block the host.
foreach (var warning in ConfigurationValidator.CollectWarnings(app.Configuration))
    app.Logger.LogWarning("Configuration warning: {Warning}", warning);

// RF-005: apply pending migrations and log the path.
// SPEC-06 RF-002: default location is beside the executable; overridable via
// KnowledgeHub:DatabasePath / Database:Path. Clear error on read-only dirs.
// SPEC-20260914-efcore-migrations: Migrate() + baseline for EnsureCreated-era DBs.
// SPEC-20260926-unified-database-provider: catalog provider resolved once in DI;
// postgres skips the sqlite dir and may receive a one-shot data copy.
using (var scope = app.Services.CreateScope())
{
    var catalog = scope.ServiceProvider.GetRequiredService<KnowledgeHub.Server.Data.CatalogDatabase>();
    if (catalog.FallbackReason is { } fb)
        app.Logger.LogError("Catalog provider fallback — {Reason}", fb);
    app.Logger.LogInformation("Catalog provider: {Provider}", catalog.Provider);
    if (!catalog.IsPostgres)
        DatabasePath.EnsureDirectory(app.Configuration);
    var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
    await DatabaseMigrator.MigrateAsync(db, app.Logger);
    if (catalog.IsPostgres)
        await KnowledgeHub.Server.Data.SqliteToPostgresMigrator.RunAsync(db, app.Configuration, app.Logger);
    else
        app.Logger.LogInformation("KnowledgeHub database ready at {Path}",
            DatabasePath.Resolve(app.Configuration));

    // SPEC-20260914-auth-login RF-001: seed admin on empty Users table.
    await AuthSeeder.SeedAsync(
        db, scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>(), app.Logger);

    // SPEC-20260914-embedding-dimension-guard: loud startup warning when the
    // persisted embeddings no longer match the configured provider.
    var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
    try
    {
        await EmbeddingCompatibilityCheck.RunAsync(db, embeddingProvider, app.Logger);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Embedding compatibility check failed — continuing startup.");
    }
}

// SPEC-05 RF-005: serve the hosted WASM client + deep-link fallback.
// MapStaticAssets resolves the #[.{fingerprint}] tokens in index.html to the
// fingerprinted asset names (UseStaticFiles would serve the literal token).
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

// SPEC-20260925-serilog-request-logging RF-002: request correlation — stable id
// on the response header + LogContext so app logs join the request event.
app.Use(async (context, next) =>
{
    using (Serilog.Context.LogContext.PushProperty("RequestId", context.TraceIdentifier))
    {
        context.Response.Headers["x-request-id"] = context.TraceIdentifier;
        await next();
    }
});

// SPEC-20260925-serilog-request-logging RF-001: one structured event per
// request; health/static noise stays at Debug, 5xx at Error.
app.UseSerilogRequestLogging(o =>
{
    o.GetLevel = (ctx, _, ex) =>
    {
        if (ex is not null || ctx.Response.StatusCode >= 500)
            return Serilog.Events.LogEventLevel.Error;
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_vs", StringComparison.OrdinalIgnoreCase))
            return Serilog.Events.LogEventLevel.Debug;
        return Serilog.Events.LogEventLevel.Information;
    };
    o.EnrichDiagnosticContext = (diag, ctx) =>
    {
        diag.Set("Caller", KnowledgeHub.McpEngine.Activity.CallerResolver.Resolve(ctx.User));
        diag.Set("ClientIp", ctx.Connection.RemoteIpAddress?.ToString());
        diag.Set("ContentLength", ctx.Response.ContentLength);
    };
});

// SPEC-20260915-apikey-usage-audit RF-002: audit every request whose principal
// authenticated via an aft_* API key (needs the post-auth claims).
app.UseMiddleware<KnowledgeHub.Server.Auth.ApiKeyUsageMiddleware>();

// SPEC-20260923-rate-limiting: after auth (partition claims) and inside the
// usage-audit middleware so rejected apikey calls are still recorded (429).
// Options resolve from the final configuration (incl. test-host overrides).
if (app.Services.GetRequiredService<KnowledgeHub.Server.RateLimiting.RateLimitOptions>().Enabled)
    app.UseRateLimiter();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains(ReadyTag)
});

// SPEC-20260915-boot-cache-revalidation RF-001: the mutable boot chain
// (index.html fallback, boot.js, the unfingerprinted blazor.webassembly.js /
// dotnet.js / dotnet.boot.js that the loader imports) must revalidate on every
// navigation — otherwise a stale cached copy keeps pointing at immutable-cached
// old fingerprints and a deploy never reaches the browser.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var bootShell = path is "/js/boot.js"
        or "/service-worker.js"
        or "/service-worker-assets.js"
        or "/manifest.webmanifest"
        or "/_framework/blazor.webassembly.js"
        or "/_framework/dotnet.js"
        or "/_framework/dotnet.boot.js"
        || (!Path.HasExtension(path)
            && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/framework-assets/", StringComparison.OrdinalIgnoreCase));
    if (bootShell)
        context.Response.OnStarting(static state =>
        {
            ((HttpResponse)state).Headers.CacheControl = "no-cache";
            return Task.CompletedTask;
        }, context.Response);
    await next();
});

app.MapStaticAssets();

// SPEC-20260915-wasm-boot-proxy-fix RF-004: extensionless mirror of
// _framework binaries — anonymous static content, same trust level as
// MapStaticAssets, so the WASM boot survives proxies that block by extension.
app.MapFrameworkAssetsApi();

// SPEC-20260914-auth-login RF-006: everything operational requires an
// authenticated principal that has cleared the password-change gate.
// Public: /api/auth/login, /health/*, static assets + SPA fallback.
app.MapAuthApi().RequireRateLimiting(GeneralPolicy);
app.MapApiKeysApi().RequireRateLimiting(GeneralPolicy);
app.MapSourcesApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapIngestionApi().RequireAuthorization(AuthPolicies.Operational);
app.MapDiagnosticsApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapSearchApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapAskApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting("llm");
app.MapAgentApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting("llm");
app.MapApprovalsApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapThreadsApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapStreamingApi(); // RequireAuthorization + RequireRateLimiting applied per-endpoint inside (returns void)
app.MapToolsApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapSettingsApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapApiKeySettingsApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapEvalApi().RequireAuthorization(AuthPolicies.Operational);
app.MapEvidenceApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
// CookieSession: the stats payload embeds flagged questions from every caller —
// API keys must not enumerate other users' queries (Devin Review PR #367).
app.MapRagEvaluationApi().RequireAuthorization(AuthPolicies.CookieSession);
// SPEC-20260928-graph-timeline-viewer RF-001: UI-only read surface over the
// temporal/episodic knowledge graph — CookieSession like the RAG dashboard.
app.MapGraphApi().RequireAuthorization(AuthPolicies.CookieSession);
app.MapSecurityApi().RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting(GeneralPolicy);
app.MapMcpInfoApi().RequireRateLimiting(GeneralPolicy);
// SPEC-20260923-observability-metrics RF-003: opt-in Prometheus scrape endpoint.
if (app.Configuration.GetValue("Telemetry:Metrics:Prometheus", false))
    app.MapPrometheusScrapingEndpoint().RequireAuthorization(AuthPolicies.Operational);
app.MapKnowledgeHubMcp().RequireAuthorization(AuthPolicies.Operational);
// SPEC-20260929-a2a-server-interop RF-001: A2A surface (card + JSON-RPC + REST).
app.MapA2AApi();
app.MapHub<McpMonitorHub>("/hubs/mcp").RequireAuthorization(AuthPolicies.Operational);
app.MapFallbackToFile("index.html");

try
{
    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Marker type so WebApplicationFactory<Program> can host the app in integration tests.
public partial class Program;
