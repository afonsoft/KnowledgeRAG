using System.Text.Json;
using A2A;
using A2A.AspNetCore;
using KnowledgeHub.Server.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace KnowledgeHub.Server.A2A;

/// <summary>
/// SPEC-20260929-a2a-server-interop: wires the A2A v1.0 surface —
/// Agent Card at <c>/.well-known/agent-card.json</c> (anonymous, per-request
/// absolute URLs), JSON-RPC + HTTP+JSON bindings at <c>/a2a</c> behind the
/// <see cref="AuthPolicies.Operational"/> policy (cookie or <c>aft_*</c> key)
/// and the <c>llm</c> rate limiter.
/// </summary>
public static class A2AEndpointExtensions
{
    private const string MimeText = "text/plain";
    private const string MimeJson = "application/json";

    /// <summary>Builds the card with absolute URLs for the given base.
    /// SPEC-20261001-a2a-task-durability RF-003/RF-005: <c>pushNotifications</c>
    /// is declared only when <c>A2a:PushNotifications:Enabled</c> is on, and
    /// every skill advertises its input/output modes for richer discovery.
    /// Enabled agent flows are appended as <c>flow_&lt;slug&gt;</c> skills —
    /// the same ids the dispatcher accepts for delegation.</summary>
    public static AgentCard BuildAgentCard(
        Uri baseUri, bool pushEnabled,
        IEnumerable<(string Slug, string? Description)>? flows = null)
    {
        var skills = new List<AgentSkill>(A2aSkillCatalog.Skills);
        if (flows is not null)
        {
            foreach (var (slug, description) in flows)
            {
                skills.Add(new AgentSkill
                {
                    Id = $"flow_{slug}",
                    Name = $"Flow: {slug}",
                    Description = description ?? $"User-defined agent flow '{slug}' — deterministic pipeline.",
                    Tags = ["flow", "pipeline"],
                    InputModes = [MimeJson],
                    OutputModes = [MimeJson],
                });
            }
        }
        var a2aUrl = new Uri(baseUri, "a2a").ToString();
        var baseUrl = baseUri.ToString().TrimEnd('/');
        return new AgentCard
        {
            Name = "KnowledgeHub",
            Description = "Knowledge MCP Hub — RAG over your knowledge sources: hybrid search, " +
                          "grounded answers, agent loop, GraphRAG, evidence receipts.",
            Version = "1.0.0",
            DocumentationUrl = $"{baseUrl}/api/mcp/capabilities",
            Provider = new AgentProvider { Organization = "KnowledgeHub", Url = baseUrl },
            SupportedInterfaces =
            [
                new AgentInterface { Url = a2aUrl, ProtocolBinding = ProtocolBindingNames.JsonRpc, ProtocolVersion = "1.0" },
                new AgentInterface { Url = a2aUrl, ProtocolBinding = ProtocolBindingNames.HttpJson, ProtocolVersion = "1.0" }
            ],
            DefaultInputModes = [MimeText, MimeJson],
            DefaultOutputModes = [MimeText, MimeJson],
            Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = pushEnabled },
            SecuritySchemes = new Dictionary<string, SecurityScheme>
            {
                ["bearer"] = new()
                {
                    HttpAuthSecurityScheme = new HttpAuthSecurityScheme
                    {
                        Scheme = "bearer",
                        BearerFormat = "aft_* API key",
                        Description = "KnowledgeHub API key — generate in /api-keys"
                    }
                }
            },
            SecurityRequirements =
            [
                new SecurityRequirement
                {
                    Schemes = new Dictionary<string, StringList>
                    {
                        ["bearer"] = new() { List = [] }
                    }
                }
            ],
            Skills = [.. skills]
        };
    }

    /// <summary>Maps the A2A endpoints. Call after auth middleware is wired.</summary>
    public static void MapA2AApi(this WebApplication app)
    {
        // Well-known card: anonymous, per-request absolute URLs (proxy-safe).
        // The push-notifications flag reads live configuration so test-host
        // overrides apply even though the DI card was built at startup.
        app.MapGet("/.well-known/agent-card.json", async (HttpContext http,
            HybridCache cache, ILoggerFactory lf, CancellationToken ct) =>
        {
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var baseUri = ResolveCardBaseUri(http, config);
            var pushEnabled = config.GetValue(KnowledgeHubA2AServer.EnabledConfigKey, true);
            // Card contents also change when the flow set changes — key the
            // cache on the catalog version so a saved/disabled flow
            // invalidates the card instead of waiting out the TTL.
            var catalogVersion = http.RequestServices
                .GetRequiredService<Mcp.IToolCatalogChangeNotifier>().Version;
            var card = await Caching.EndpointCache.GetJsonAsync(cache,
                $"a2a:card:{baseUri}:{pushEnabled}:{catalogVersion}",
                async c =>
                {
                    var db = http.RequestServices.GetRequiredService<Data.KnowledgeHubDbContext>();
                    var flows = await db.AgentFlows
                        .Where(f => f.Enabled)
                        .Select(f => new { f.Slug, f.Description })
                        .ToListAsync(c);
                    return BuildAgentCard(baseUri, pushEnabled,
                        flows.Select(f => (f.Slug, f.Description)));
                },
                lf, ct);
            return Results.Json(card, A2AJsonUtilities.DefaultOptions);
        }).AllowAnonymous();

        var handler = app.Services.GetRequiredService<IA2ARequestHandler>();
        var card0 = app.Services.GetRequiredService<AgentCard>();

        app.MapA2A(handler, "/a2a")
            .RequireAuthorization(AuthPolicies.Operational)
            .RequireRateLimiting("llm");
        app.MapHttpA2A(handler, card0, "/a2a")
            .RequireAuthorization(AuthPolicies.Operational)
            .RequireRateLimiting("llm");
    }

    /// <summary>Base for the per-request card URLs. An explicit
    /// <c>A2A:BaseUrl</c> wins — the documented knob for TLS-terminating
    /// proxies where the origin only ever sees http. Otherwise honor the
    /// first <c>X-Forwarded-Proto</c> hop, then the connection scheme.</summary>
    internal static Uri ResolveCardBaseUri(HttpContext http, IConfiguration config)
    {
        if (config["A2A:BaseUrl"] is { Length: > 0 } configured
            && Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri))
            return configuredUri;
        var proto = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var scheme = string.IsNullOrWhiteSpace(proto)
            ? http.Request.Scheme
            : proto.Split(',')[0].Trim();
        return new Uri($"{scheme}://{http.Request.Host}/");
    }
}
