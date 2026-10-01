using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace KnowledgeHub.Server.Auth;

/// <summary>Authorization policy names (SPEC-20260914-auth-login RF-006/RF-009).</summary>
public static class AuthPolicies
{
    /// <summary>Cookie or API key, no password gate — auth surface (me/logout/change-password).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Cookie or API key + password gate — every business surface (/api/*, /mcp, /hubs/*).</summary>
    public const string Operational = "Operational";

    /// <summary>Cookie session only + gate — API key management (keys cannot manage keys).</summary>
    public const string CookieSession = "CookieSession";

    public static readonly string[] AnyScheme =
        [CookieAuthenticationDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName];
}

/// <summary>Fails while the cookie principal carries `pwd_changed=false`; API keys always satisfy it.</summary>
public sealed class PasswordChangedRequirement : IAuthorizationRequirement;

public sealed class PasswordChangedHandler : AuthorizationHandler<PasswordChangedRequirement>
{
    public const string FailureReason = "password_change_required";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PasswordChangedRequirement requirement)
    {
        if (context.User.FindFirst(ApiKeyAuthenticationHandler.AuthMethodClaim)?.Value == "apikey"
            || context.User.FindFirst(ApiKeyAuthenticationHandler.PasswordChangedClaim)?.Value == "true")
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, FailureReason));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Emits `403 {"error":"password_change_required"}` for gate denials so the SPA
/// can route to /change-password; every other result (success, challenge,
/// generic forbid) is delegated to the default handler — bypassing it would
/// skip the 401/403 enforcement entirely.
/// </summary>
public sealed class PasswordGateResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons
                .Any(r => r.Message == PasswordChangedHandler.FailureReason) == true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { error = PasswordChangedHandler.FailureReason }, context.RequestAborted);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
