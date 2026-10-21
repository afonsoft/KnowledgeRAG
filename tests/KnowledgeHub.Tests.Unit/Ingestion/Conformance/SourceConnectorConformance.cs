using System.Net;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Ingestion.Conformance;

// SPEC-20261021-connector-conformance-suite: shared contract every
// ISourceConnector implementation must satisfy (langchain-tests analogue).
// Each concrete fixture below supplies its connector + a valid source; the
// base asserts the FetchResult contract, incremental stub semantics and the
// no-secret-leak rule.

/// <summary>Abstract conformance fixture. Subclass once per connector.</summary>
public abstract class SourceConnectorConformance
{
    /// <summary>Planted in the fake secret store — must never appear in outputs.</summary>
    protected const string SecretSentinel = "S3CR3T-SENTINEL-7f9a2b";

    /// <summary>Connector under test, wired to the fixture's fakes.</summary>
    protected abstract ISourceConnector CreateConnector();

    /// <summary>A source whose config resolves against the fixture's fakes and
    /// yields at least one document.</summary>
    protected abstract KnowledgeSource CreateValidSource();

    /// <summary>The SourceType the connector must advertise.</summary>
    protected abstract SourceType ExpectedType { get; }

    /// <summary>Whether this connector reads secrets (the sentinel is planted).</summary>
    protected virtual bool UsesSecrets => false;

    [Fact]
    public void Type_Matches_Expected()
    {
        Assert.Equal(ExpectedType, CreateConnector().Type);
    }

    [Fact]
    public async Task FetchResult_Collections_NeverNull()
    {
        var result = await CreateConnector().FetchAsync(CreateValidSource(), CancellationToken.None);

        Assert.NotNull(result.Documents);
        Assert.NotNull(result.Warnings);
    }

    [Fact]
    public async Task Documents_Have_Required_Fields()
    {
        var result = await CreateConnector().FetchAsync(CreateValidSource(), CancellationToken.None);

        Assert.NotEmpty(result.Documents);
        foreach (var doc in result.Documents)
        {
            Assert.False(string.IsNullOrWhiteSpace(doc.UriReference));
            Assert.False(string.IsNullOrWhiteSpace(doc.Title));
            // full doc → text; incremental stub → fingerprint must be present
            Assert.True(
                !string.IsNullOrEmpty(doc.TextContent) || doc.Fingerprint is not null,
                $"doc '{doc.UriReference}' has neither text nor fingerprint");
        }
    }

    [Fact]
    public async Task Secrets_Never_Leak_Into_Outputs()
    {
        if (!UsesSecrets)
            return;

        var result = await CreateConnector().FetchAsync(CreateValidSource(), CancellationToken.None);

        var haystack = string.Join('\n',
            result.Documents.SelectMany(d => new[] { d.UriReference, d.Title, d.TextContent, d.Fingerprint ?? "" })
                .Concat(result.Warnings)
                .Concat(result.FailedUris ?? []));
        Assert.DoesNotContain(SecretSentinel, haystack);
    }

    // ---------- shared fakes ----------

    protected sealed class RouteTable
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<(string Method, string Url)> Calls { get; } = [];

        public RouteTable Map(string url, string body,
            HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
        {
            _routes[url] = _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
            return this;
        }

        public RouteTable MapFunc(string url, Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _routes[url] = handler;
            return this;
        }

        public HttpResponseMessage Route(HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add((request.Method.Method, url));
            if (_routes.TryGetValue(url, out var handler))
                return handler(request);
            // fall back to any registered prefix (query strings, paging)
            var prefix = _routes.Keys.FirstOrDefault(k => url.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            return prefix is not null
                ? _routes[prefix](request)
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found", Encoding.UTF8, "text/plain")
                };
        }
    }

    protected sealed class RouterHandler(RouteTable routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(routes.Route(request));
    }

    protected sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Returns <see cref="SecretSentinel"/> for every provider key —
    /// plants the sentinel wherever a connector resolves a secret.</summary>
    protected class SentinelSecrets : IIntegrationSecretStore
    {
        private readonly string? _value;
        public SentinelSecrets(string? value = SecretSentinel) => _value = value;
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(_value);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(false);
    }

    protected static KnowledgeSource Source(SourceType type, object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"conformance-{type}",
        SourceType = type,
        ConfigurationJson = System.Text.Json.JsonSerializer.Serialize(configuration)
    };
}

/// <summary>Extra contract for <see cref="IIncrementalSourceConnector"/>:
/// a second fetch with known fingerprints must keep URIs (stub or full doc).</summary>
public abstract class IncrementalSourceConnectorConformance : SourceConnectorConformance
{
    protected abstract IIncrementalSourceConnector CreateIncrementalConnector();

    [Fact]
    public async Task Incremental_KnownFingerprints_KeepUris()
    {
        var connector = CreateIncrementalConnector();
        var source = CreateValidSource();

        var first = await connector.FetchAsync(source, CancellationToken.None);
        Assert.NotEmpty(first.Documents);

        var existing = first.Documents
            .Where(d => d.Fingerprint is not null)
            .ToDictionary(d => d.UriReference, d => d.Fingerprint!);
        if (existing.Count == 0)
            return; // connector fingerprints nothing — nothing to assert incrementally

        var second = await connector.FetchAsync(source, existing, CancellationToken.None);

        // every previously-seen URI must still be present (stub or full)
        var uris = second.Documents.Select(d => d.UriReference).ToHashSet();
        foreach (var uri in existing.Keys)
            Assert.Contains(uri, uris);

        // unchanged docs come back as stubs: empty text + preserved fingerprint
        foreach (var doc in second.Documents.Where(d => string.IsNullOrEmpty(d.TextContent)))
            Assert.NotNull(doc.Fingerprint);
    }
}
