using KnowledgeHub.Server.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Unit.Memory;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-004: bank resolution order —
/// path /mcp/banks/{key}/ → X-Memory-Bank header → bankKey arg → default.
/// </summary>
public class MemoryBankContextTests
{
    private static IServiceProvider Scoped(MemoryBankContext? ctx)
    {
        var services = new ServiceCollection();
        if (ctx is not null)
            services.AddScoped(_ => ctx);
        return services.BuildServiceProvider().CreateScope().ServiceProvider;
    }

    [Fact]
    public void RF004_transport_binding_beats_argument()
    {
        var sp = Scoped(new MemoryBankContext { BankKey = "do-header" });
        Assert.Equal("do-header", MemoryBankContext.EffectiveKey(sp, "do-arg"));
    }

    [Fact]
    public void RF004_argument_used_when_unscoped()
    {
        var sp = Scoped(new MemoryBankContext());
        Assert.Equal("do-arg", MemoryBankContext.EffectiveKey(sp, "do-arg"));
    }

    [Fact]
    public async Task RF004_path_prefix_captures_key_and_rewrites()
    {
        var bankCtx = new MemoryBankContext();
        string? rewrittenPath = null;
        var mw = new MemoryBankMiddleware(ctx =>
        {
            rewrittenPath = ctx.Request.Path.Value;
            return Task.CompletedTask;
        });
        var http = new DefaultHttpContext();
        http.Request.Path = "/mcp/banks/projeto-x/";
        await mw.InvokeAsync(http, bankCtx);
        Assert.Equal("projeto-x", bankCtx.BankKey);
        Assert.Equal("/mcp/", rewrittenPath);
    }

    [Fact]
    public async Task RF004_header_binds_bank_on_plain_mcp()
    {
        var bankCtx = new MemoryBankContext();
        var mw = new MemoryBankMiddleware(_ => Task.CompletedTask);
        var http = new DefaultHttpContext();
        http.Request.Path = "/mcp";
        http.Request.Headers["X-Memory-Bank"] = "agente-42";
        await mw.InvokeAsync(http, bankCtx);
        Assert.Equal("agente-42", bankCtx.BankKey);
    }

    [Fact]
    public async Task RF004_empty_bank_segment_is_400()
    {
        var mw = new MemoryBankMiddleware(_ => Task.CompletedTask);
        var http = new DefaultHttpContext();
        http.Request.Path = "/mcp/banks/";
        http.Response.Body = new MemoryStream();
        await mw.InvokeAsync(http, new MemoryBankContext());
        Assert.Equal(400, http.Response.StatusCode);
    }
}
