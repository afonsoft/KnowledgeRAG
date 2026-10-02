using KnowledgeHub.Server.Configuration;
using KnowledgeHub.Server.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260917-onnx-local-embeddings RF-001/RF-002: provider loading,
// factory selection, startup validation, vector contract. Model-dependent
// tests are gated on the artifact directory — absent → early return (the
// model is never committed to git).
public class OnnxEmbeddingProviderTests
{
    private static readonly string ModelDir = Path.Join(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "all-MiniLM-L6-v2");

    private static bool ModelPresent =>
        File.Exists(Path.Join(ModelDir, OnnxEmbeddingProvider.ModelFileName))
        && File.Exists(Path.Join(ModelDir, OnnxEmbeddingProvider.VocabFileName));

    private static EmbeddingProviderFactoryHolder Factory() => new();

    [Fact]
    public void Load_MissingModel_ThrowsClearError()
    {
        var ex = Assert.Throws<EmbeddingProviderException>(
            () => OnnxEmbeddingProvider.Load(Path.Join(Path.GetTempPath(), $"no-model-{Guid.NewGuid():N}")));
        Assert.Contains("model.onnx", ex.Message);
        Assert.Contains("huggingface.co", ex.Message);
    }

    [Fact]
    public void Load_NullPath_UsesDefaultDirectory()
    {
        // Default dir doesn't exist in the test environment → clear error.
        if (Directory.Exists(OnnxEmbeddingProvider.DefaultModelDirectory) && ModelPresent)
            return;
        var ex = Assert.Throws<EmbeddingProviderException>(() => OnnxEmbeddingProvider.Load(null));
        Assert.Contains("ModelPath", ex.Message);
    }

    [Fact]
    public void Factory_OnnxWithoutModel_Throws()
    {
        var options = new EmbeddingOptions
        {
            Provider = "onnx",
            ModelPath = Path.Join(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")
        };
        Assert.Throws<EmbeddingProviderException>(() =>
            EmbeddingProviderFactory.Create(options, Factory().HttpClientFactory));
    }

    [Fact]
    public void Factory_UnknownProvider_StillFallsBackToDeterministic()
    {
        // RF-002: unknown providers keep the deterministic fallback.
        var provider = EmbeddingProviderFactory.Create(
            new EmbeddingOptions { Provider = "bogus" }, Factory().HttpClientFactory);
        Assert.IsType<DeterministicEmbeddingProvider>(provider);
    }

    [Fact]
    public void Validator_OnnxWithoutModel_FailsWithGuidance()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embeddings:Provider"] = "onnx",
            ["Embeddings:ModelPath"] = Path.Join(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigurationValidator.Validate(cfg));
        Assert.Contains("Provider=onnx", ex.Message);
        Assert.Contains("ModelPath", ex.Message);
    }

    [Fact]
    public void Validator_OnnxWrongDimensions_Fails()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embeddings:Provider"] = "onnx",
            ["Embeddings:Dimensions"] = "768"
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigurationValidator.Validate(cfg));
        Assert.Contains("384", ex.Message);
    }

    [Fact]
    [Trait("Category", "RequiresOnnxModel")]
    public async Task Embed_Produces384DeterministicNormalizedVector()
    {
        // CA-001: gated on real model artifacts.
        if (!ModelPresent)
            return;

        using var provider = OnnxEmbeddingProvider.Load(ModelDir);
        Assert.Equal("onnx:all-MiniLM-L6-v2", provider.ModelId);
        Assert.Equal(384, provider.Dimensions);

        var a = await provider.EmbedAsync("semantic search over documents");
        var b = await provider.EmbedAsync("semantic search over documents");
        var c = await provider.EmbedAsync("completely different topic about cooking recipes");

        Assert.Equal(384, a.Length);
        Assert.Equal(a, b); // deterministic

        var norm = Math.Sqrt(a.Sum(v => v * v));
        Assert.InRange(norm, 0.99, 1.01); // L2-normalized

        var related = await provider.EmbedAsync("document retrieval with embeddings");
        var simRelated = Cosine(a, related);
        var simUnrelated = Cosine(a, c);
        Assert.True(simRelated > simUnrelated,
            $"expected related ({simRelated:F3}) > unrelated ({simUnrelated:F3})");
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot; // both already L2-normalized
    }

    private sealed class EmbeddingProviderFactoryHolder : IHttpClientFactory
    {
        private readonly HttpClient _client = new();
        public IHttpClientFactory HttpClientFactory => this;
        public HttpClient CreateClient(string name) => _client;
    }
}
