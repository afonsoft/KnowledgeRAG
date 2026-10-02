using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260915-wasm-boot-proxy-fix RF-004/RF-006: extensionless
// /framework-assets/{fileName} mirror of wwwroot/_framework so proxies that
// block downloads by extension (.dat/.wasm/...) cannot break the WASM boot.
public class FrameworkAssetsTests
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

    private static IFileInfo PickAsset(IWebHostEnvironment env, string extension)
    {
        var file = env.WebRootFileProvider.GetDirectoryContents("_framework")
            .FirstOrDefault(f => !f.IsDirectory && f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(file); // test host must expose _framework static web assets
        return file;
    }

    private static bool HasSibling(IWebHostEnvironment env, string fileName, string suffix) =>
        env.WebRootFileProvider.GetFileInfo($"_framework/{fileName}{suffix}").Exists;

    [Fact]
    public void FontAwesome_StaticWebAsset_Exists_AndIndexReferencesIt()
    {
        // Covers #134: icon-only Buttons rendered empty because Font Awesome CSS
        // was never loaded. Guards both ends — the static web asset must resolve
        // and index.html must reference it.
        using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();

        const string cssPath = "_content/BootstrapBlazor.FontAwesome/css/font-awesome.min.css";
        Assert.True(env.WebRootFileProvider.GetFileInfo(cssPath).Exists,
            $"missing static web asset {cssPath}");

        var index = env.WebRootFileProvider.GetFileInfo("index.html");
        Assert.True(index.Exists);
        using var reader = new StreamReader(index.CreateReadStream());
        Assert.Contains("BootstrapBlazor.FontAwesome", reader.ReadToEnd());
    }

    [Fact]
    public async Task Get_KnownDatAsset_Anonymous_Returns200OctetStreamImmutableCache()
    {
        // Covers AC: remapped binary asset → 200 anonymously, octet-stream, immutable cache
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var name = PickAsset(env, ".dat").Name;
        using var client = factory.CreateClient(); // no auth — must serve pre-login

        var response = await client.GetAsync($"/framework-assets/{name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Get_KnownWasmAsset_Returns200()
    {
        // Covers AC: *.wasm binaries are also servable through the mirror route
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var name = PickAsset(env, ".wasm").Name;
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
    }

    [Fact]
    public async Task Get_UnknownName_Returns404()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/framework-assets/no-such-file.bin");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("..foo")]
    [InlineData("..%2Fsecret")]
    [InlineData("a%2Fb")]
    [InlineData("..%5Cwin")]
    [InlineData("bad%20name")]
    public async Task Get_TraversalOrIllegalChars_Returns404(string badName)
    {
        // Covers AC: nothing outside _framework is ever served
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{badName}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_DotDot_NormalizedAway_NeverReturnsBinaryContent()
    {
        // "%2E%2E" is normalized to "/" before routing and lands on the SPA
        // fallback — acceptable (same exposure as MapFallbackToFile today),
        // but it must never surface _framework-external binary content.
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/framework-assets/%2E%2E");

        Assert.NotEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_AcceptEncoding_ServesCompressedSibling_WhenPresent()
    {
        // Covers RF-004 best-effort: publish emits .br/.gz next to every asset;
        // dev emits .gz — serve the negotiated variant when it exists.
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var name = PickAsset(env, ".dat").Name;
        var hasBr = HasSibling(env, name, ".br");
        var hasGz = HasSibling(env, name, ".gz");
        Assert.True(hasBr || hasGz, "expected at least one compressed sibling for " + name);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/framework-assets/{name}");
        request.Headers.AcceptEncoding.ParseAdd("gzip, br");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var encoding = response.Content.Headers.ContentEncoding.ToString();
        Assert.True(
            hasBr ? encoding == "br" : encoding == "gzip",
            $"expected Content-Encoding {(hasBr ? "br" : "gzip")}, got '{encoding}'");
    }

    // ---- SPEC-20260915-wasm-boot-proxy-hardening --------------------------
    // Proxies match blocked extensions in the request URL itself, so the
    // mirror must also drop the suffix: /framework-assets/{stem}/{ext}.

    private static (string Stem, string Ext) Split(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        Assert.True(dot > 0, "fingerprinted asset must carry an extension: " + fileName);
        return (fileName[..dot], fileName[(dot + 1)..]);
    }

    [Fact]
    public async Task GetStemExt_KnownDatAsset_Returns200ImmutableCache()
    {
        // Covers RF-001/AC: extensionless URL serves the same bytes anonymously
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var (stem, ext) = Split(PickAsset(env, ".dat").Name);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{stem}/{ext}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task GetStemExt_KnownWasmAsset_ReturnsApplicationWasm()
    {
        // Covers RF-002: correct MIME restores WebAssembly.instantiateStreaming
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var (stem, ext) = Split(PickAsset(env, ".wasm").Name);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{stem}/{ext}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/wasm", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetStemExt_EncB64_ReturnsTextPlainRoundTrip()
    {
        // Covers RF-003: base64 body decodes byte-for-byte to the real asset so
        // the client-side SHA-256 check matches the boot integrity hash.
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var file = PickAsset(env, ".dat");
        var (stem, ext) = Split(file.Name);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{stem}/{ext}?enc=b64");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
        await using var expected = new MemoryStream();
        await file.CreateReadStream().CopyToAsync(expected);
        Assert.Equal(expected.ToArray(), Convert.FromBase64String(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task GetStemExt_EncJunk_FallsBackToRawBytes()
    {
        // Covers RF-003 edge: only enc=b64 activates the encoded variant.
        await using var factory = new Fixture();
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var (stem, ext) = Split(PickAsset(env, ".dat").Name);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{stem}/{ext}?enc=zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetStemExt_UnknownStem_Returns404()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/framework-assets/no-such-file.abc123/dat");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("..foo", "dat")]          // traversal fragment in stem
    [InlineData("ok.stem", "D4T")]        // uppercase ext rejected (regex is [a-z0-9])
    [InlineData("ok.stem", "d-at")]       // illegal char in ext
    [InlineData("bad%20stem", "dat")]     // illegal char in stem
    public async Task GetStemExt_TraversalOrIllegalChars_Returns404(string stem, string ext)
    {
        // Covers AC: nothing outside _framework is ever served via the split route
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/framework-assets/{stem}/{ext}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
