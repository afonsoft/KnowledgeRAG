using KnowledgeHub.Server.Embeddings;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Embeddings;

// Covers SPEC-20260927-voyage-and-cohere-embeddings T5: factory selection of
// the new providers and the sub-options / top-level fallback merge.
public class EmbeddingProviderFactoryTests
{
    private static EmbeddingOptions OptionsFor(string provider, string? apiKey = "pa-test") => new()
    {
        Provider = provider,
        ApiKey = apiKey,
        Model = "fallback-model",
        Dimensions = 384,
        Voyage = new() { ApiKey = apiKey, Model = "voyage-3", Dimensions = 1024 },
        Cohere = new() { ApiKey = apiKey, Model = "embed-multilingual-v3.0", Dimensions = 1024 }
    };

    [Fact]
    public void Create_Voyage_SelectsVoyageProvider()
    {
        var p = EmbeddingProviderFactory.Create(OptionsFor("voyage"), new FakeClientFactory());
        Assert.IsType<VoyageAiEmbeddingProvider>(p);
        Assert.Equal("voyage:voyage-3", p.ModelId);
        Assert.Equal(1024, p.Dimensions);
    }

    [Fact]
    public void Create_Cohere_SelectsCohereProvider()
    {
        var p = EmbeddingProviderFactory.Create(OptionsFor("cohere"), new FakeClientFactory());
        Assert.IsType<CohereEmbeddingProvider>(p);
        Assert.Equal("cohere:embed-multilingual-v3.0", p.ModelId);
        Assert.Equal(1024, p.Dimensions);
    }

    [Fact]
    public void Create_Voyage_NoSubOptions_FallsBackToTopLevel()
    {
        var opts = OptionsFor("voyage");
        opts.Voyage = new(); // empty sub-options → fall back to top-level
        opts.Model = "voyage-code-3";
        opts.Dimensions = 1536;
        var p = EmbeddingProviderFactory.Create(opts, new FakeClientFactory());
        Assert.Equal("voyage:voyage-code-3", p.ModelId);
        Assert.Equal(1536, p.Dimensions);
    }

    private sealed class FakeClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
