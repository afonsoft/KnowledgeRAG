using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260926-embeddings-runtime-coherence RF-001/RF-002: with a fixed-schema
// vector store (sqlite-vec) the PUT must block dims ≠ store dims, the GET must
// expose storeDimensions/dimsMismatch, and a provider that cannot build
// surfaces as providerError — never a 500.
public class EmbeddingDimsGuardApiTests : IClassFixture<EmbeddingDimsGuardApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-embguard-{Guid.NewGuid():N}.db"),
                    ["Embeddings:Provider"] = "deterministic",
                    ["Embeddings:Dimensions"] = "384",
                    ["VectorStore:Provider"] = "sqlite-vec"
                }));
    }

    private readonly Fixture _factory;
    public EmbeddingDimsGuardApiTests(Fixture factory) => _factory = factory;

    private async Task<HttpClient> AuthedCleanAsync()
    {
        var http = await TestAuth.LoginAsync(_factory);
        (await http.DeleteAsync("/api/settings/embeddings")).EnsureSuccessStatusCode();
        return http;
    }

    [Fact]
    public async Task Put_DimsDifferentFromStore_Returns400()
    {
        var http = await AuthedCleanAsync();

        var res = await http.PutAsJsonAsync("/api/settings/embeddings",
            new { provider = "deterministic", dimensions = 512 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("384", body);
    }

    [Fact]
    public async Task Get_ExposesStoreDimensions_NoMismatch()
    {
        var http = await AuthedCleanAsync();

        using var doc = JsonDocument.Parse(
            await http.GetStringAsync("/api/settings/embeddings"));

        Assert.Equal(384, doc.RootElement.GetProperty("storeDimensions").GetInt32());
        Assert.False(doc.RootElement.GetProperty("dimsMismatch").GetBoolean());
    }

    [Fact]
    public async Task Get_BrokenProvider_ReturnsProviderError_Not500()
    {
        var http = await AuthedCleanAsync();

        // SPEC-20260926-embeddings-swap-safety RF-001: the PUT now probes the
        // model's real output — a dir without artifacts is rejected at save
        // time (previously accepted, breaking the provider at runtime).
        var missingDir = Directory.CreateTempSubdirectory("kh-onnx-missing-");
        try
        {
            var rejected = await http.PutAsJsonAsync("/api/settings/embeddings", new
            {
                provider = "onnx",
                modelPath = missingDir.FullName,
                dimensions = 384
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        finally
        {
            missingDir.Delete(recursive: true);
        }

        // The GET fail-soft path still matters for env-configured breakage —
        // exercise it by accepting a valid model path, then deleting it so
        // the runtime provider fails at build.
        var modelDir = CopyModelToTemp();
        if (modelDir is null)
            return; // models/ is gitignored — no artifacts in this environment
        try
        {
            (await http.PutAsJsonAsync("/api/settings/embeddings", new
            {
                provider = "onnx",
                modelPath = modelDir,
                dimensions = 384
            })).EnsureSuccessStatusCode();
            Directory.Delete(modelDir, recursive: true);

            var response = await http.GetAsync("/api/settings/embeddings");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.Equal("onnx", root.GetProperty("provider").GetString());
            Assert.Null(root.GetProperty("stampedModelId").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("providerError").GetString()));
        }
        finally
        {
            // Restore env config so other tests see a healthy provider.
            (await http.DeleteAsync("/api/settings/embeddings")).EnsureSuccessStatusCode();
        }
    }

    private static string? CopyModelToTemp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "models", "all-MiniLM-L6-v2");
            var model = Path.Join(candidate, "model.onnx");
            var vocab = Path.Join(candidate, "vocab.txt");
            if (File.Exists(model) && File.Exists(vocab))
            {
                var target = Path.Join(Path.GetTempPath(), $"kh-onnx-{Guid.NewGuid():N}");
                Directory.CreateDirectory(target);
                File.Copy(model, Path.Join(target, "model.onnx"));
                File.Copy(vocab, Path.Join(target, "vocab.txt"));
                return target;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
