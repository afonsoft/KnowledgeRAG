using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260915-boot-cache-revalidation: the mutable WASM boot chain
// must revalidate on every navigation so a deploy cannot be pinned by stale
// cached mutable files pointing at immutable-cached old fingerprints.
public class BootCacheHeadersTests
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api-keys")]
    [InlineData("/login")]
    public async Task Get_SpaShell_ReturnsNoCache(string path)
    {
        // Covers RF-001: index.html fallback must always revalidate
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_BootJs_ReturnsNoCache()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/js/boot.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_BlazorWebAssemblyJs_ReturnsNoCache()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/_framework/blazor.webassembly.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_DotNetJs_ReturnsNoCache()
    {
        // dotnet.js is imported unfingerprinted by blazor.webassembly.js — must revalidate
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/_framework/dotnet.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_FingerprintedWasm_StillServed()
    {
        // Covers RF-002: fingerprinted assets keep their platform cache policy —
        // the middleware only overrides the mutable boot chain
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var name = env.WebRootFileProvider.GetDirectoryContents("_framework")
            .First(f => !f.IsDirectory && f.Name.EndsWith(".wasm", StringComparison.Ordinal)).Name;
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/_framework/{name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ApiEndpoint_DoesNotGetNoCache()
    {
        // Covers RF-002: /api/* paths are not part of the boot shell
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.NotEqual("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_FrameworkAssetsMirror_KeepsImmutableCache()
    {
        // Covers RF-002: the extensionless mirror keeps its own immutable header
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var name = env.WebRootFileProvider.GetDirectoryContents("_framework")
            .First(f => !f.IsDirectory && f.Name.EndsWith(".dat", StringComparison.Ordinal)).Name;
        var dot = name.LastIndexOf('.');
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{name[..dot]}/{name[(dot + 1)..]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }
}
