using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Settings;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Settings tools (SPEC-20260916-api-key-settings RF-004 + SPEC-20260926-split-settings-tools):
/// set_api_key_settings — allows an API key to configure integration API keys (firecrawl, deepwiki, tavily, context7).
/// set_chat_settings — allows an API key to configure chat LLM endpoint, model and API key.
/// </summary>
public sealed class SettingsToolsProvider : IToolProvider
{
    private static readonly JsonObject SetApiKeySettingsSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "provider":{"type":"string","enum":["firecrawl","deepwiki","tavily","context7"],"description":"Which integration provider to configure"},
          "apiKey":{"type":["string","null"],"description":"API key override (null or empty = inherit from global)"}
        },"required":["provider"],
        "examples":[{"provider":"deepwiki","apiKey":"dw-secret-key"},{"provider":"firecrawl","apiKey":null}]}
        """)!.AsObject();

    private static readonly JsonObject SetChatSettingsSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "endpoint":{"type":["string","null"],"description":"OpenAI-compatible base URL (null = inherit from global)"},
          "model":{"type":["string","null"],"description":"Model name (null = inherit from global)"},
          "apiKey":{"type":["string","null"],"description":"Chat provider API key override (null = inherit from global)"}
        },
        "examples":[{"endpoint":"http://localhost:11434","model":"llama3","apiKey":null}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "set_api_key_settings",
                Title = "Set API key settings",
                Description = "Override integration API keys (firecrawl, deepwiki, tavily, context7) for the current API key. Pass null or empty apiKey to remove the override and inherit from global. Only available to API-key-authenticated sessions.",
                InputSchema = SetApiKeySettingsSchema,
                ReadOnly = false,
                IdempotentHint = true,
                Handler = async (ctx, ct) =>
                {
                    var keyId = RequireApiKeySession(ctx);
                    var provider = ToolArgs.RequiredString(ctx, "provider");
                    var service = ctx.Services!.GetRequiredService<IApiKeyChatSettingsService>();
                    return await SaveOrRemoveIntegrationKeyAsync(service, keyId, provider, ctx, ct);
                }
            },
            new CatalogTool
            {
                Name = "set_chat_settings",
                Title = "Set chat settings",
                Description = "Override chat LLM settings (endpoint, model, API key) for the current API key. Null fields inherit from global defaults. Only available to API-key-authenticated sessions.",
                InputSchema = SetChatSettingsSchema,
                ReadOnly = false,
                IdempotentHint = true,
                Handler = async (ctx, ct) =>
                {
                    var http = ctx.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                    if (http is null)
                        throw new McpProtocolException("HTTP context not available", McpErrorCode.InternalError);

                    var authMethod = http.User.FindFirst(ApiKeyAuthenticationHandler.AuthMethodClaim)?.Value;
                    var keyIdValue = http.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
                    if (authMethod != "apikey" || !Guid.TryParse(keyIdValue, out var keyId))
                        throw new McpProtocolException("This tool is only available to API-key-authenticated sessions", McpErrorCode.InvalidParams);

                    var endpoint = ToolArgs.OptionalString(ctx, "endpoint");
                    var model = ToolArgs.OptionalString(ctx, "model");
                    var apiKey = ToolArgs.OptionalString(ctx, "apiKey");

                    var service = ctx.Services!.GetRequiredService<IApiKeyChatSettingsService>();
                    await service.SaveAsync(keyId, endpoint, model, apiKey, ct);
                    var result = await service.DescribeAsync(keyId, ct);

                    var msg = $"Chat settings updated for API key '{keyId}'.\n" +
                              $"Provider: {result.Provider}\n" +
                              $"Endpoint: {result.Endpoint ?? "(inherited)"}\n" +
                              $"Model: {result.Model ?? "(inherited)"}\n" +
                              $"Has override: {result.HasOverride}\n" +
                              $"Override fields: {string.Join(", ", result.OverrideFields)}";
                    return await ToolResults.Text(msg);
                }
            }
        ];
        return Task.FromResult(tools);
    }

    /// <summary>Resolves the caller's API-key identity or throws — the settings
    /// tools are only available to API-key-authenticated sessions.</summary>
    private static Guid RequireApiKeySession(ToolCallContext ctx)
    {
        var http = ctx.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http is null)
            throw new McpProtocolException("HTTP context not available", McpErrorCode.InternalError);

        var authMethod = http.User.FindFirst(ApiKeyAuthenticationHandler.AuthMethodClaim)?.Value;
        var keyIdValue = http.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value;
        if (authMethod != "apikey" || !Guid.TryParse(keyIdValue, out var keyId))
            throw new McpProtocolException("This tool is only available to API-key-authenticated sessions", McpErrorCode.InvalidParams);
        return keyId;
    }

    /// <summary>Saves or removes the integration key override for one provider.</summary>
    private static async ValueTask<CallToolResult> SaveOrRemoveIntegrationKeyAsync(
        IApiKeyChatSettingsService service, Guid keyId, string provider,
        ToolCallContext ctx, CancellationToken ct)
    {
        if (provider is not ("firecrawl" or "deepwiki" or "tavily" or "context7"))
            throw new McpProtocolException($"Unknown provider '{provider}'", McpErrorCode.InvalidParams);

        var apiKey = ToolArgs.OptionalString(ctx, "apiKey");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await service.SaveIntegrationKeyAsync(keyId, provider, apiKey, ct);
            return await ToolResults.Text($"{provider} API key saved for API key '{keyId}'.");
        }

        await service.RemoveIntegrationKeyAsync(keyId, provider, ct);
        return await ToolResults.Text($"{provider} API key removed for API key '{keyId}' — falls back to global.");
    }
}
