using System.Security.Claims;
using System.Text.Encodings.Web;
using KnowledgeHub.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Auth;

/// <summary>
/// `Authorization: Bearer aft_*` scheme for non-browser clients
/// (SPEC-20260914-auth-login RF-005). Also honors `?access_token=` under
/// /hubs/* (SignalR) and GET /mcp/sse (legacy MCP SSE) — both are consumed
/// by EventSource/WebSocket-style clients that cannot set request headers.
/// Non-`aft_` tokens → NoResult so other schemes still apply.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    /// <summary>Principals authenticated by an API key carry this claim — they bypass the password-change gate.</summary>
    public const string AuthMethodClaim = "auth_method";
    public const string PasswordChangedClaim = "pwd_changed"; // NOSONAR S2068 — nome de claim, não segredo
    /// <summary>SPEC-20260915-apikey-usage-audit RF-001: ties audited requests to the presenting key.</summary>
    public const string KeyIdClaim = "key_id";

    private static readonly TimeSpan LastUsedWriteThrottle = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();
        if (token is null || !token.StartsWith(ApiKeyService.KeyPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var db = Context.RequestServices.GetRequiredService<KnowledgeHubDbContext>();
        var key = await ApiKeyService.FindActiveAsync(db, token, Context.RequestAborted);
        if (key?.User is null)
            return AuthenticateResult.Fail("invalid or revoked api key");

        var now = DateTimeOffset.UtcNow;
        if (key.LastUsedAt is null || now - key.LastUsedAt > LastUsedWriteThrottle)
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, key.User.Id.ToString()),
            new Claim(ClaimTypes.Name, key.User.Username),
            new Claim(AuthMethodClaim, "apikey"),
            new Claim(KeyIdClaim, key.Id.ToString()),
            new Claim(PasswordChangedClaim, "true")
        ], Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string? ExtractToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        // SPEC-20260917-sse-e2e-prod RF-003: header-less transports may carry
        // the key as ?access_token=. Scoped to the two paths that need it —
        // /hubs (SignalR) and /mcp/sse (EventSource has no header support).
        var headerlessPath =
            Request.Path.StartsWithSegments("/hubs")
            || Request.Path.Equals("/mcp/sse", StringComparison.OrdinalIgnoreCase);
        if (headerlessPath
            && Request.Query.TryGetValue("access_token", out var accessToken))
            return accessToken.ToString();

        return null;
    }
}
