using KnowledgeHub.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// Caller identity extraction for tool handlers (SPEC-20260922
/// per-key-integration-secrets RF-001). Reads the API-key claim from the
/// ambient <see cref="IHttpContextAccessor"/> — the same surface
/// <c>set_api_key_settings</c> uses. Never throws: cookie sessions, missing
/// context and malformed claims all yield null (global-resolution path).
/// </summary>
internal static class CallerIdentity
{
    /// <summary>API-key id of the calling session, or null when the caller is
    /// not apikey-authenticated (cookie admin UI, anonymous) or the claim is
    /// missing/invalid.</summary>
    public static Guid? TryGetApiKeyId(ToolCallContext ctx)
    {
        var http = ctx.Services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http is null || http.User.FindFirst(ApiKeyAuthenticationHandler.AuthMethodClaim)?.Value != "apikey")
            return null;
        return Guid.TryParse(http.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value, out var id)
            ? id
            : null;
    }
}
