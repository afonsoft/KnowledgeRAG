using System.Security.Claims;
using System.Text.Json;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;

namespace KnowledgeHub.Server.Auth;

/// <summary>
/// /api/apikeys management (SPEC-20260914-auth-login RF-010) — cookie sessions
/// only: API keys cannot create or revoke API keys.
/// </summary>
public static class ApiKeyEndpoints
{
    private const string ApiKeyNotFound = "api key não encontrada";
    private const string ListScope = "list:apikeys";

    public static RouteGroupBuilder MapApiKeysApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/apikeys")
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapDelete("/{id:guid}", RevokeAsync);
        group.MapGet("/{id:guid}/usage", UsageAsync);
        group.MapGet("/{id:guid}/secret", RevealSecretAsync);

        // SPEC-20260923-source-authorization §5: scope admin lives under the
        // dashed /api/api-keys surface but is cookie-only like this group —
        // keys can never scope keys.
        app.MapPut("/api/api-keys/{id:guid}/scopes", SetScopesAsync)
            .RequireAuthorization(AuthPolicies.CookieSession);

        // SPEC-20260923-per-key-rate-limits RF-004: per-key rate-limit
        // override admin — cookie-only like the rest of key management.
        app.MapPut("/api/api-keys/{id:guid}/rate-limit", SetRateLimitAsync)
            .RequireAuthorization(AuthPolicies.CookieSession);
        app.MapDelete("/api/api-keys/{id:guid}/rate-limit", ClearRateLimitAsync)
            .RequireAuthorization(AuthPolicies.CookieSession);

        // Per-key write permission — cookie-only like the rest of key admin.
        app.MapPut("/api/api-keys/{id:guid}/write-access", SetWriteAccessAsync)
            .RequireAuthorization(AuthPolicies.CookieSession);

        return group;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, KnowledgeHubDbContext db,
        HybridCache cache, ILoggerFactory lf, CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        // Caller-scoped — the user id joins the discriminator so the shared
        // version token never leaks one admin's key list to another.
        var list = await EndpointCache.GetJsonAsync(cache, $"{ListScope}:u:{userId:N}", async c =>
            {
                var keys = await db.ApiKeys
            .Where(k => k.UserId == userId)
            .Select(k => new
            {
                k.Id,
                k.Name,
                k.Prefix,
                k.CreatedAt,
                k.LastUsedAt,
                k.RevokedAt,
                k.AllowedSourceIdsJson,
                k.AllowedToolsJson,
                k.LlmRateLimitPermits,
                k.LlmRateLimitWindowSeconds,
                k.SyncRateLimitPermits,
                k.SyncRateLimitWindowSeconds,
                k.AllowWrite
            })
                    .ToListAsync(c);
                // SQLite cannot ORDER BY DateTimeOffset — sort client-side.
                return keys
                    .OrderByDescending(k => k.CreatedAt)
                    .Select(k =>
                    {
                        var scope = CallerScope.FromJson(k.Id, k.AllowedSourceIdsJson, k.AllowedToolsJson, k.AllowWrite);
                        return new ApiKeyDto(
                            k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt,
                            scope.AllowedSourceIds?.ToList(), scope.AllowedTools?.ToList(),
                            k.LlmRateLimitPermits, k.LlmRateLimitWindowSeconds,
                            k.SyncRateLimitPermits, k.SyncRateLimitWindowSeconds,
                            k.AllowWrite);
                    })
                    .ToList();
            }, lf, ct, tags: [ListScope]);
        return Results.Ok(list ?? []);
    }

    private static async Task<IResult> CreateAsync(
        CreateApiKeyRequest request,
        HttpContext http,
        KnowledgeHubDbContext db,
        IDataProtectionProvider dataProtection,
        HybridCache cache,
        ICacheInvalidationBus bus,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
            return Results.BadRequest(new { error = "nome é obrigatório (máx. 100 caracteres)" });

        var secret = ApiKeyService.GenerateKey();
        var protector = dataProtection.CreateProtector("api-keys");
        var key = new ApiKey
        {
            Name = name,
            KeyHash = ApiKeyService.HashKey(secret),
            Prefix = ApiKeyService.PrefixOf(secret),
            ProtectedKey = protector.Protect(secret),
            UserId = CurrentUserId(http)
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);

        return Results.Json(
            new ApiKeyCreatedDto(key.Id, key.Name, key.Prefix, secret),
            statusCode: StatusCodes.Status201Created);
    }

    // SPEC-20260915-apikey-usage-audit RF-004: owner-only usage summary +
    // recent audit events. SQLite cannot ORDER BY DateTimeOffset — aggregate
    // and sort client-side (bounded by the 10k-per-key retention cap).
    private static async Task<IResult> UsageAsync(
        Guid id, HttpContext http, KnowledgeHubDbContext db, CancellationToken ct)
    {
        var owns = await db.ApiKeys
            .AnyAsync(k => k.Id == id && k.UserId == CurrentUserId(http), ct);
        if (!owns)
            return Results.NotFound(new { error = ApiKeyNotFound });

        var events = await db.ApiKeyUsageEvents
            .Where(e => e.ApiKeyId == id)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var errors = events.Count(e => e.StatusCode >= 400);
        var recent = events
            .OrderByDescending(e => e.Timestamp)
            .Take(100)
            .Select(e => new ApiKeyUsageEventDto(
                e.Id, e.Timestamp, e.HttpMethod, e.Path, e.StatusCode, e.DurationMs, e.UserAgent))
            .ToList();

        return Results.Ok(new ApiKeyUsageDto(
            events.Count,
            events.Count(e => e.Timestamp >= now.AddDays(-1)),
            events.Count(e => e.Timestamp >= now.AddDays(-7)),
            events.Count > 0 ? events.Average(e => e.DurationMs) : 0,
            errors,
            events.Count > 0 ? (double)errors / events.Count : 0,
            recent));
    }

    private static async Task<IResult> RevokeAsync(
        Guid id, HttpContext http, KnowledgeHubDbContext db,
        HybridCache cache, ICacheInvalidationBus bus, ILoggerFactory lf, CancellationToken ct)
    {
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == CurrentUserId(http), ct);
        if (key is null)
            return Results.NotFound(new { error = ApiKeyNotFound });

        key.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
        return Results.NoContent();
    }

    /// <summary>SPEC-20260924-api-key-reveal-and-copy RF-002: owner-only key secret reveal endpoint.</summary>
    private static async Task<IResult> RevealSecretAsync(
        Guid id,
        HttpContext http,
        KnowledgeHubDbContext db,
        IDataProtectionProvider dataProtection,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId, ct);
        if (key is null)
            return Results.NotFound(new { error = ApiKeyNotFound });

        if (string.IsNullOrEmpty(key.ProtectedKey))
            return Results.Ok(new ApiKeySecretDto(key.Id, null, false));

        try
        {
            var protector = dataProtection.CreateProtector("api-keys");
            var secret = protector.Unprotect(key.ProtectedKey);
            return Results.Ok(new ApiKeySecretDto(key.Id, secret, true));
        }
        catch (Exception ex)
        {
            var logger = loggerFactory.CreateLogger(typeof(ApiKeyEndpoints));
            logger.LogWarning(ex, "Failed to unprotect API key {KeyId}", id);
            return Results.Ok(new ApiKeySecretDto(key.Id, null, false));
        }
    }

    /// <summary>SPEC-20260923-source-authorization RF-001: replaces the key's
    /// source/tool allowlists. Unknown source ids or tool names → 400; the
    /// cached scope is evicted so the next request sees the change.</summary>
    private static async Task<IResult> SetScopesAsync(
        Guid id,
        SetApiKeyScopesRequest? body,
        HttpContext http,
        KnowledgeHubDbContext db,
        Mcp.IDynamicToolCatalog catalog,
        IMemoryCache memory,
        HybridCache cache,
        ICacheInvalidationBus bus,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == CurrentUserId(http), ct);
        if (key is null)
            return Results.NotFound(new { error = ApiKeyNotFound });

        if (body?.AllowedSourceIds is { } sourceIds && sourceIds.Count > 0)
        {
            var known = await db.Sources
                .Where(s => sourceIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct);
            var unknown = sourceIds.Except(known).ToList();
            if (unknown.Count > 0)
                return Results.BadRequest(new { error = $"unknown source id(s): {string.Join(", ", unknown)}" });
        }

        if (body?.AllowedTools is { } tools && tools.Count > 0)
        {
            var available = (await catalog.GetUnfilteredToolsAsync(http.RequestServices, ct))
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = tools.Where(t => !available.Contains(t)).ToList();
            if (unknown.Count > 0)
                return Results.BadRequest(new { error = $"unknown tool(s): {string.Join(", ", unknown)}" });
        }

        key.AllowedSourceIdsJson = body?.AllowedSourceIds is null
            ? null
            : JsonSerializer.Serialize(body.AllowedSourceIds);
        key.AllowedToolsJson = body?.AllowedTools is null
            ? null
            : JsonSerializer.Serialize(body.AllowedTools);
        await db.SaveChangesAsync(ct);

        memory.Remove(CallerScopeProvider.CacheKey(id));
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
        return Results.NoContent();
    }

    /// <summary>SPEC-20260923-per-key-rate-limits RF-004: sets the key's
    /// rate-limit override. Every field nullable — null inherits the global
    /// RateLimiting:* value. The resolver cache is invalidated so the next
    /// partition sees the change.</summary>
    private static async Task<IResult> SetRateLimitAsync(
        Guid id,
        SetApiKeyRateLimitRequest? body,
        HttpContext http,
        KnowledgeHubDbContext db,
        RateLimiting.IApiKeyRateLimitResolver resolver,
        HybridCache cache,
        ICacheInvalidationBus bus,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == CurrentUserId(http), ct);
        if (key is null)
            return Results.NotFound(new { error = ApiKeyNotFound });

        if (new[] { body?.LlmPermits, body?.SyncPermits }.Any(v => v is < 1 or > 100_000))
            return Results.BadRequest(new { error = "permits must be 1..100000" });
        if (new[] { body?.LlmWindowSeconds, body?.SyncWindowSeconds }.Any(v => v is < 1 or > 86_400))
            return Results.BadRequest(new { error = "windowSeconds must be 1..86400" });

        key.LlmRateLimitPermits = body?.LlmPermits;
        key.LlmRateLimitWindowSeconds = body?.LlmWindowSeconds;
        key.SyncRateLimitPermits = body?.SyncPermits;
        key.SyncRateLimitWindowSeconds = body?.SyncWindowSeconds;
        await db.SaveChangesAsync(ct);

        resolver.Invalidate();
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
        return Results.NoContent();
    }

    /// <summary>Per-key write gate: desligada a chave vira somente-leitura —
    /// tools não-readonly respondem um isError informativo. O scope cache é
    /// evictado para o próximo request já ver a mudança.</summary>
    private static async Task<IResult> SetWriteAccessAsync(
        Guid id,
        SetApiKeyWriteAccessRequest? body,
        HttpContext http,
        KnowledgeHubDbContext db,
        IMemoryCache memory,
        HybridCache cache,
        ICacheInvalidationBus bus,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == CurrentUserId(http), ct);
        if (key is null)
            return Results.NotFound(new { error = ApiKeyNotFound });

        key.AllowWrite = body?.AllowWrite ?? true;
        await db.SaveChangesAsync(ct);

        memory.Remove(CallerScopeProvider.CacheKey(id));
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
        return Results.NoContent();
    }

    /// <summary>Limpa o override — a key volta aos limites globais.</summary>
    private static async Task<IResult> ClearRateLimitAsync(
        Guid id,
        HttpContext http,
        KnowledgeHubDbContext db,
        RateLimiting.IApiKeyRateLimitResolver resolver,
        HybridCache cache,
        ICacheInvalidationBus bus,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == CurrentUserId(http), ct);
        if (key is null)
            return Results.NotFound(new { error = ApiKeyNotFound });

        key.LlmRateLimitPermits = null;
        key.LlmRateLimitWindowSeconds = null;
        key.SyncRateLimitPermits = null;
        key.SyncRateLimitWindowSeconds = null;
        await db.SaveChangesAsync(ct);

        resolver.Invalidate();
        await EndpointCache.EvictTagAsync(cache, bus, ListScope, lf, ct);
        return Results.NoContent();
    }

    private static Guid CurrentUserId(HttpContext http) =>
        Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
