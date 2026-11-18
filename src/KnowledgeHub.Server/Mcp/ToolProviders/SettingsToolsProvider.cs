using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Settings;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Unified settings tool (consolidation wave 1):
/// <c>set_settings</c> — configures per-key integration API keys or chat LLM settings.
/// Replaces the former <c>set_api_key_settings</c> and <c>set_chat_settings</c> tools.
/// Legacy aliases are kept for backward compatibility (deprecated).
/// </summary>
public sealed class SettingsToolsProvider : IToolProvider
{
    private static readonly JsonObject UnifiedSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "scope":{"type":"string","enum":["chat","integration"],"description":"'chat' configures LLM endpoint/model/apiKey; 'integration' configures upstream provider keys (firecrawl, deepwiki, tavily, context7)"},
          "provider":{"type":"string","enum":["firecrawl","deepwiki","tavily","context7"],"description":"Required when scope=integration: which integration provider to configure"},
          "apiKey":{"type":["string","null"],"description":"API key override (null or empty = inherit from global)"},
          "endpoint":{"type":["string","null"],"description":"scope=chat only: OpenAI-compatible base URL (null = inherit from global)"},
          "model":{"type":["string","null"],"description":"scope=chat only: Model name (null = inherit from global)"}
        },"required":["scope"],
        "examples":[
          {"scope":"integration","provider":"tavily","apiKey":"tvly-xxx"},
          {"scope":"chat","endpoint":"https://api.openai.com/v1","model":"gpt-4o","apiKey":"sk-xxx"}
        ]}
        """)!.AsObject();

    // Legacy schemas kept for alias resolution
    private static readonly JsonObject SetApiKeySettingsSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "provider":{"type":"string","enum":["firecrawl","deepwiki","tavily","context7"],"description":"Which integration provider to configure"},
          "apiKey":{"type":["string","null"],"description":"API key override (null or empty = inherit from global)"}
        },"required":["provider"],
        "examples":[{"provider":"tavily","apiKey":"tvly-xxx"}]}
        """)!.AsObject();

    private static readonly JsonObject SetChatSettingsSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "endpoint":{"type":["string","null"],"description":"OpenAI-compatible base URL (null = inherit from global)"},
          "model":{"type":["string","null"],"description":"Model name (null = inherit from global)"},
          "apiKey":{"type":["string","null"],"description":"Chat provider API key override (null = inherit from global)"}
        },
        "examples":[{"endpoint":"https://api.openai.com/v1","model":"gpt-4o","apiKey":"sk-xxx"}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "set_settings",
                Title = "Configure settings",
                Description = "Configure per-key settings. scope='chat' sets LLM endpoint/model/apiKey; scope='integration' sets upstream provider keys (firecrawl, deepwiki, tavily, context7). Replaces the deprecated set_chat_settings and set_api_key_settings tools.",
                InputSchema = UnifiedSchema,
                ReadOnly = false,
                Handler = HandleUnifiedAsync
            },
            // --- deprecated aliases (backward compat) ---
            new CatalogTool
            {
                Name = "set_api_key_settings",
                Title = "[Deprecated] Set integration API key",
                Description = "DEPRECATED — use set_settings(scope='integration') instead. Configures integration API keys.",
                InputSchema = SetApiKeySettingsSchema,
                ReadOnly = false,
                Handler = HandleLegacyIntegrationAsync
            },
            new CatalogTool
            {
                Name = "set_chat_settings",
                Title = "[Deprecated] Set chat LLM settings",
                Description = "DEPRECATED — use set_settings(scope='chat') instead. Configures chat LLM endpoint, model and API key.",
                InputSchema = SetChatSettingsSchema,
                ReadOnly = false,
                Handler = HandleLegacyChatAsync
            }
        ];

        return Task.FromResult(tools);
    }

    // ── Unified handler ──
    private static async ValueTask<CallToolResult> HandleUnifiedAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var scope = ToolArgs.RequiredString(ctx, "scope");
        return scope switch
        {
            "integration" => await HandleIntegrationCoreAsync(ctx, ct),
            "chat" => await HandleChatCoreAsync(ctx, ct),
            _ => await ToolResults.Error($"Unknown scope '{scope}'. Use 'chat' or 'integration'.")
        };
    }

    // ── Legacy alias handlers ──
    private static ValueTask<CallToolResult> HandleLegacyIntegrationAsync(ToolCallContext ctx, CancellationToken ct)
        => HandleIntegrationCoreAsync(ctx, ct);

    private static ValueTask<CallToolResult> HandleLegacyChatAsync(ToolCallContext ctx, CancellationToken ct)
        => HandleChatCoreAsync(ctx, ct);

    // ── Core logic ──
    private static Guid RequireApiKeySession(ToolCallContext ctx)
    {
        var http = ctx.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http is null)
            throw new McpProtocolException("HTTP context not available", McpErrorCode.InternalError);
        if (http.Items[CallerIdentity.ItemKey] is not CallerIdentity { ApiKeyId: { } keyId })
            throw new McpProtocolException("API key session required", McpErrorCode.InternalError);
        return keyId;
    }

    private static async ValueTask<CallToolResult> HandleIntegrationCoreAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var provider = ToolArgs.RequiredString(ctx, "provider");
        var apiKey = ToolArgs.OptionalString(ctx, "apiKey");
        var keyId = RequireApiKeySession(ctx);
        var service = ctx.Services.GetRequiredService<IApiKeyChatSettingsService>();

        if (!string.IsNullOrEmpty(apiKey))
        {
            await service.SaveIntegrationKeyAsync(keyId, provider, apiKey, ct);
            return await ToolResults.Text($"{provider} API key saved for API key '{keyId}'.");
        }

        await service.RemoveIntegrationKeyAsync(keyId, provider, ct);
        return await ToolResults.Text($"{provider} API key removed for API key '{keyId}' — falls back to global.");
    }

    private static async ValueTask<CallToolResult> HandleChatCoreAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var endpoint = ToolArgs.OptionalString(ctx, "endpoint");
        var model = ToolArgs.OptionalString(ctx, "model");
        var apiKey = ToolArgs.OptionalString(ctx, "apiKey");
        var keyId = RequireApiKeySession(ctx);
        var service = ctx.Services.GetRequiredService<IApiKeyChatSettingsService>();

        await service.SaveAsync(keyId, endpoint, model, apiKey, ct);
        return await ToolResults.Text($"Chat settings saved for API key '{keyId}'.");
    }
}
