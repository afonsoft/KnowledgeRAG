using System.Text.Json;
using A2A;
using A2A.AspNetCore;
using KnowledgeHub.Server.Auth;

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
    /// every skill advertises its input/output modes for richer discovery.</summary>
    public static AgentCard BuildAgentCard(Uri baseUri, bool pushEnabled)
    {
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
            Skills =
            [
                new AgentSkill
                {
                    Id = "ask_knowledge",
                    Name = "Ask knowledge",
                    Description = "Grounded Q&A over the indexed knowledge base — returns a synthesized answer with [n] citations.",
                    Tags = ["rag", "qa", "knowledge"],
                    Examples = ["What changed in the last release?"],
                    InputModes = [MimeText, MimeJson],
                    OutputModes = [MimeText, MimeJson]
                },
                new AgentSkill
                {
                    Id = "search_knowledge",
                    Name = "Search knowledge",
                    Description = "Hybrid semantic + lexical search across all active sources — ranked passages with provenance.",
                    Tags = ["search", "retrieval"],
                    Examples = ["rate limiting policy"],
                    InputModes = [MimeText, MimeJson],
                    OutputModes = [MimeText, MimeJson]
                },
                new AgentSkill
                {
                    Id = "agent_chat",
                    Name = "Agent chat",
                    Description = "Multi-turn agentic loop with tool-calling over the live catalog.",
                    Tags = ["agent", "chat", "tools"],
                    Examples = ["Summarize today's ingestion run"],
                    InputModes = [MimeText],
                    OutputModes = [MimeText, MimeJson]
                },
                new AgentSkill
                {
                    Id = "read_document",
                    Name = "Read document",
                    Description = "Reads a full markdown document from a connected vault by path — input is a JSON object with a `path` field.",
                    Tags = ["docs", "read"],
                    Examples = ["roadmap/2026.md"],
                    InputModes = [MimeJson],
                    OutputModes = [MimeText]
                },
                new AgentSkill
                {
                    Id = "write_knowledge",
                    Name = "Write knowledge",
                    Description = "Creates a document in the connected vault — input is a JSON object with `title` and `content` (optional `source`, `tags`). Requires a write-capable credential.",
                    Tags = ["docs", "write", "knowledge"],
                    Examples = ["{\"title\": \"Runbook\", \"content\": \"Restart steps…\"}"],
                    InputModes = [MimeJson],
                    OutputModes = [MimeText]
                },
                new AgentSkill
                {
                    Id = "write_note",
                    Name = "Write note",
                    Description = "Writes a markdown note into the Obsidian vault — input is a JSON object with `title` and `content` (optional `path`, `tags`). Requires a write-capable credential.",
                    Tags = ["docs", "write", "obsidian"],
                    Examples = ["{\"title\": \"Daily log\", \"content\": \"…\"}"],
                    InputModes = [MimeJson],
                    OutputModes = [MimeText]
                }
            ]
        };
    }

    /// <summary>Maps the A2A endpoints. Call after auth middleware is wired.</summary>
    public static void MapA2AApi(this WebApplication app)
    {
        // Well-known card: anonymous, per-request absolute URLs (proxy-safe).
        // The push-notifications flag reads live configuration so test-host
        // overrides apply even though the DI card was built at startup.
        app.MapGet("/.well-known/agent-card.json", (HttpContext http) =>
        {
            var baseUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}/");
            var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
            var card = BuildAgentCard(baseUri,
                cfg.GetValue(KnowledgeHubA2AServer.EnabledConfigKey, true));
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
}
