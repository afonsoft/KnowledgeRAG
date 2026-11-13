using KnowledgeHub.Server.Context;
using Microsoft.AspNetCore.Http;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-004: per-request memory-bank binding.
/// Resolution order: path <c>/mcp/banks/{bankKey}/</c> → <c>X-Memory-Bank</c>
/// header → tool argument <c>bankKey</c> → "default". Path/header win over
/// the tool argument by design (an explicitly scoped endpoint implies the bank).
/// </summary>
public sealed class MemoryBankContext
{
    /// <summary>Bank key bound by the transport (path/header); null = unscoped.</summary>
    public string? BankKey { get; set; }

    /// <summary>Effective bank for a call: transport binding beats the arg.
    /// SPEC-20261113 RF-004: when no bankKey exists anywhere, a caller-supplied
    /// <paramref name="repo"/> slug derives <c>repo:owner/name</c> — the
    /// name-drift antidote (agents keying banks by their repo converge).</summary>
    public static string? EffectiveKey(IServiceProvider sp, string? arg, string? repo = null)
    {
        var bound = sp.GetService<MemoryBankContext>()?.BankKey ?? arg;
        if (bound is not null)
            return bound;
        return RepoScope.TryParse(NormalizeGitRemote(repo), out var scope) ? $"repo:{scope.Key}" : null;
    }

    /// <summary>Git remotes arrive as <c>git@host:owner/repo.git</c> or bare
    /// <c>host/owner/repo[.git]</c> — coerce both into a form
    /// <see cref="RepoScope"/> accepts (https URL or owner/name).</summary>
    private static string? NormalizeGitRemote(string? repo)
    {
        if (repo is not { Length: > 0 } raw)
            return repo;
        var v = raw.Trim();
        if (v.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = v.IndexOf(':');
            if (colon > 4)
                v = $"https://{v[4..colon]}/{v[(colon + 1)..]}";
        }
        else if (!v.Contains("://") && v.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            v = $"https://{v}";
        return v;
    }
}

/// <summary>
/// Binds <see cref="MemoryBankContext"/> for MCP requests: rewrites
/// <c>/mcp/banks/{key}/…</c> to <c>/mcp/…</c> with the key captured, and
/// honors <c>X-Memory-Bank</c> on plain <c>/mcp</c> calls. Unknown banks are
/// rejected lazily — bank resolution validates existence against the caller's
/// key scope and answers a clear tool/REST error.
/// </summary>
public sealed class MemoryBankMiddleware(RequestDelegate next)
{
    private const string Prefix = "/mcp/banks/";

    public async Task InvokeAsync(HttpContext ctx, MemoryBankContext bankContext)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[Prefix.Length..];
            var slash = rest.IndexOf('/');
            var key = slash < 0 ? rest : rest[..slash];
            if (key.Length == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new
                { error = "memory bank key missing in /mcp/banks/{key}/" }, ctx.RequestAborted);
                return;
            }
            bankContext.BankKey = Uri.UnescapeDataString(key);
            ctx.Request.Path = "/mcp" + (slash < 0 ? "" : rest[slash..]);
        }
        else if (ctx.Request.Headers.TryGetValue("X-Memory-Bank", out var header)
                 && header.ToString().Trim() is { Length: > 0 } headerKey)
        {
            bankContext.BankKey = headerKey;
        }
        await next(ctx);
    }
}
