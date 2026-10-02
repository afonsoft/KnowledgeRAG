using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260926-embeddings-swap-safety: Asymmetric.Auto in the provider
// signature (RF-002), lease-drained disposal on swap (RF-003) and the ONNX
// model reporting its real output width (RF-001).
public sealed class EmbeddingsSwapTests
{
    private sealed class MutableSettings(EmbeddingOptions options) : IEmbeddingSettingsService
    {
        public EmbeddingOptions Options { get; set; } = options;
        public EmbeddingOptions GetEffectiveOptions() => Options;
        public (int, int) GetChunking() => (500, 50);
        public Task<EmbeddingSettingsDto> DescribeAsync(CancellationToken ct = default) =>
            Task.FromResult<EmbeddingSettingsDto>(null!);
        public Task SaveAsync(SaveEmbeddingSettingsRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task RemoveKeyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private sealed class FakeHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TrackedProvider : IEmbeddingProvider, IDisposable
    {
        public bool Disposed;
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new[] { 1f, 0f, 0f, 0f }).ToList());
        public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default) => EmbedAsync(text, ct);
        public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default) => EmbedAsync(text, ct);
        public Task<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            EmbedBatchAsync(texts, ct);
        public void Dispose() => Disposed = true;
    }

    // ---- RF-002: Asymmetric.Auto participates in the provider signature ----

    [Fact]
    public void Signature_Changes_WhenAsymmetricAutoFlips()
    {
        var on = new EmbeddingOptions { Provider = "openai" };
        on.Asymmetric.Enabled = true;
        on.Asymmetric.Auto = true;
        var off = new EmbeddingOptions { Provider = "openai" };
        off.Asymmetric.Enabled = true;
        off.Asymmetric.Auto = false;

        Assert.NotEqual(
            EmbeddingProviderResolver.Signature(on),
            EmbeddingProviderResolver.Signature(off));
    }

    // ---- RF-003: the previous provider drains outstanding leases before
    // dispose — in-flight inference never hits a disposed session.

    [Fact]
    public async Task Swap_WaitsForLeases_BeforeDisposingPrevious()
    {
        var settings = new MutableSettings(new EmbeddingOptions { Provider = "deterministic", Dimensions = 4 });
        var built = new List<TrackedProvider>();
        var resolver = new EmbeddingProviderResolver(settings, new FakeHttpFactory(),
            NullLogger<EmbeddingProviderResolver>.Instance,
            _ => { var p = new TrackedProvider(); built.Add(p); return p; });

        var lease = resolver.Acquire();
        var previous = built[0];

        settings.Options = new EmbeddingOptions { Provider = "ollama", Dimensions = 4 };
        _ = resolver.Current; // trigger the swap

        await Task.Delay(200);
        Assert.False(previous.Disposed); // drain in progress — lease held

        lease.Dispose();
        for (var i = 0; i < 100 && !previous.Disposed; i++)
            await Task.Delay(20);
        Assert.True(previous.Disposed); // drained → disposed
    }

    // ---- RF-001: the ONNX provider reports the model's real output width ----

    [Fact]
    public void OnnxProvider_Dimensions_ReadsModelMetadata()
    {
        var dir = FindModelDir();
        if (dir is null)
            return; // models/ is gitignored — probe only when artifacts exist

        using var provider = OnnxEmbeddingProvider.Load(dir);
        Assert.Equal(384, provider.Dimensions); // all-MiniLM-L6-v2
    }

    private static string? FindModelDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "models", "all-MiniLM-L6-v2");
            if (File.Exists(Path.Join(candidate, "model.onnx")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
