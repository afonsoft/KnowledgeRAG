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

    /// <summary>Effective bank for a call: transport binding beats the arg.</summary>
    public static string? EffectiveKey(IServiceProvider sp, string? arg) =>
        sp.GetService<MemoryBankContext>()?.BankKey ?? arg;
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
