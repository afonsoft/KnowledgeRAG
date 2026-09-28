using System.Net;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-git-repository-source-connector: repoUrl/tuple
// resolution, recursive tree + glob filtering, PAT auth, commit-SHA
// incremental fast path, per-blob fault tolerance and SSRF guard.
public sealed class GitRepositoryConnectorTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private sealed class FakeGit
    {
        public int Calls;
        public string? SeenAuth;
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler;

        public HttpResponseMessage Route(HttpRequestMessage request)
        {
            Calls++;
            request.Headers.TryGetValues("Authorization", out var a);
            request.Headers.TryGetValues("PRIVATE-TOKEN", out var p);
            SeenAuth = a?.FirstOrDefault() ?? p?.FirstOrDefault();
            return Handler?.Invoke(request)
                ?? JsonResponse("""{"commit":{"sha":"abc123"}}""");
        }
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TextResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>Routes branch→sha, tree and blob requests by URL shape.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> GitHubRoutes(
        string sha = "abc123", string treeJson = TreeJson,
        Func<string, string>? blob = null, Func<string, HttpResponseMessage?>? blobResponse = null)
        => req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/branches/main", StringComparison.Ordinal))
                return JsonResponse("""{"commit":{"sha":"SHA"} }""".Replace("SHA", sha));
            if (path.Contains("/git/trees/"))
                return JsonResponse(treeJson);
            if (path.Contains("/contents/"))
            {
                var file = Uri.UnescapeDataString(
                    path[(path.IndexOf("/contents/", StringComparison.Ordinal) + 10)..]);
                return blobResponse?.Invoke(file)
                    ?? TextResponse(blob?.Invoke(file) ?? $"# {file}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private const string TreeJson = """
        {"tree":[
          {"path":"README.md","type":"blob","size":120,"sha":"b1"},
          {"path":"docs/guide.md","type":"blob","size":90,"sha":"b2"},
          {"path":".git/config","type":"blob","size":10,"sha":"b3"},
          {"path":"node_modules/pkg/index.js","type":"blob","size":10,"sha":"b4"},
          {"path":"src/main.cs","type":"blob","size":10,"sha":"b5"},
          {"path":"docs","type":"tree","sha":"t1"}
        ],"truncated":false}
        """;

    private sealed class RouterHandler(FakeGit api) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(api.Route(request));
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeSecrets(string? key = null) : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(key);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static GitRepositoryConnector Sut(FakeGit api, string? pat = null) =>
        new(new FakeFactory(new RouterHandler(api)), new FakeSecrets(pat),
            NullLogger<GitRepositoryConnector>.Instance);

    private static KnowledgeSource Source(object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "git",
        SourceType = SourceType.GitRepository,
        ConfigurationJson = JsonSerializer.Serialize(configuration, Json)
    };

    private static object Config(object? extra = null)
    {
        var base_ = new Dictionary<string, object?>
        {
            ["provider"] = "github",
            ["owner"] = "weaviate",
            ["name"] = "Verba",
            ["branch"] = "main"
        };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties())
                base_[p.Name] = p.GetValue(extra);
        return base_;
    }

    // AC-1: markdown files matching includePatterns are downloaded → RawDocument.
    [Fact]
    public async Task PublicRepo_DownloadsMatchingMarkdown()
    {
        var git = new FakeGit { Handler = GitHubRoutes() };
        var result = await Sut(git).FetchAsync(Source(Config()), CancellationToken.None);

        var paths = result.Documents.Select(d => d.UriReference).ToList();
        Assert.Contains(paths, u => u.Contains("README.md"));
        Assert.Contains(paths, u => u.Contains("docs/guide.md"));
        Assert.DoesNotContain(paths, u => u.Contains(".git/config"));
        Assert.DoesNotContain(paths, u => u.Contains("node_modules"));
        Assert.DoesNotContain(paths, u => u.Contains("src/main.cs"));
        Assert.All(result.Documents, d => Assert.Equal(
            $"git://github/weaviate/Verba@main:{d.UriReference.Split(':').Last()}", d.UriReference));
        Assert.All(result.Documents, d =>
            Assert.StartsWith("git:github:weaviate/Verba:main:abc123:", d.Fingerprint));
    }

    // AC-2: same commit SHA → NoChanges stubs, zero blob downloads.
    [Fact]
    public async Task UnchangedCommit_ReturnsStubsWithoutDownloads()
    {
        var git = new FakeGit { Handler = GitHubRoutes() };
        var sut = Sut(git);
        var source = Source(Config());

        var first = await sut.FetchAsync(source, CancellationToken.None);
        var existing = first.Documents.ToDictionary(d => d.UriReference, d => d.Fingerprint!);

        var blobRequests = 0;
        git.Handler = GitHubRoutes(blobResponse: _ => { blobRequests++; return TextResponse("x"); });

        var second = await sut.FetchAsync(source, existing, CancellationToken.None);
        Assert.Equal(existing.Count, second.Documents.Count);
        Assert.All(second.Documents, d => Assert.Empty(d.TextContent));
        Assert.Equal(0, blobRequests);
    }

    // AC-3: PAT goes out as Authorization Bearer for private repos.
    [Fact]
    public async Task PrivateRepo_SendsBearerToken()
    {
        var git = new FakeGit { Handler = GitHubRoutes() };
        await Sut(git, pat: "ghp_secret").FetchAsync(Source(Config()), CancellationToken.None);
        Assert.Equal("Bearer ghp_secret", git.SeenAuth);
    }

    // AC-4: excludePatterns are enforced (already covered partially — test explicit patterns).
    [Fact]
    public async Task ExcludePatterns_AreEnforced()
    {
        var git = new FakeGit
        {
            Handler = GitHubRoutes(treeJson: """
                {"tree":[
                  {"path":"docs/a.md","type":"blob","size":10,"sha":"b1"},
                  {"path":"docs/draft.md","type":"blob","size":10,"sha":"b2"}
                ],"truncated":false}
                """)
        };
        var result = await Sut(git).FetchAsync(
            Source(Config(new { excludePatterns = new[] { "**/draft.md" } })),
            CancellationToken.None);
        Assert.Single(result.Documents);
        Assert.Contains("docs/a.md", result.Documents[0].UriReference);
    }

    // Edge: missing branch → clear sync failure.
    [Fact]
    public async Task MissingBranch_ThrowsClearError()
    {
        var git = new FakeGit
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut(git).FetchAsync(Source(Config(new { branch = "ghost" })),
                CancellationToken.None));
        Assert.Contains("ghost", ex.Message);
    }

    // Edge: empty repo → empty documents, no failure.
    [Fact]
    public async Task EmptyRepo_ReturnsEmptyWithoutFailure()
    {
        var git = new FakeGit
        {
            Handler = req => req.RequestUri!.AbsolutePath.Contains("/git/trees/")
                ? JsonResponse("""{"tree":[],"truncated":false}""")
                : JsonResponse("""{"commit":{"sha":"abc"}}""")
        };
        var result = await Sut(git).FetchAsync(Source(Config()), CancellationToken.None);
        Assert.Empty(result.Documents);
        Assert.Empty(result.Warnings);
    }

    // RF-002: files over maxFileSizeBytes are skipped with a warning.
    [Fact]
    public async Task OversizedFiles_AreSkipped()
    {
        var git = new FakeGit
        {
            Handler = GitHubRoutes(treeJson: """
                {"tree":[
                  {"path":"big.md","type":"blob","size":9999999,"sha":"b1"},
                  {"path":"small.md","type":"blob","size":10,"sha":"b2"}
                ],"truncated":false}
                """)
        };
        var result = await Sut(git).FetchAsync(
            Source(Config(new { maxFileSizeBytes = 100000 })),
            CancellationToken.None);
        Assert.Single(result.Documents);
        Assert.Contains("small.md", result.Documents[0].UriReference);
        Assert.Contains(result.Warnings, w => w.Contains("maxFileSizeBytes"));
    }

    // RF-003: per-file download failure → warning + FailedUris, others survive.
    [Fact]
    public async Task BlobFailure_IsPerFile()
    {
        var git = new FakeGit
        {
            Handler = GitHubRoutes(blobResponse: f => f.Contains("README")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : TextResponse("ok"))
        };
        var result = await Sut(git).FetchAsync(Source(Config()), CancellationToken.None);
        Assert.Contains(result.Warnings, w => w.Contains("README.md"));
        Assert.NotNull(result.FailedUris);
        Assert.Contains(result.FailedUris!, u => u.Contains("README.md"));
        Assert.DoesNotContain(result.Documents.Select(d => d.UriReference), u => u.Contains("README.md"));
        Assert.Contains(result.Documents, d => d.UriReference.Contains("guide.md"));
    }

    // SSRF: private instanceUrl requires explicit opt-in.
    [Fact]
    public async Task PrivateInstanceUrl_RequiresOptIn()
    {
        var git = new FakeGit();
        var config = Source(new
        {
            provider = "gitea",
            instanceUrl = "http://192.168.1.10:3000",
            owner = "o",
            name = "r"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut(git).FetchAsync(config, CancellationToken.None));
    }

    // RF-001: repoUrl resolution (github.com URL → owner/name/provider).
    [Fact]
    public async Task RepoUrl_ResolvesOwnerAndName()
    {
        var git = new FakeGit { Handler = GitHubRoutes() };
        var result = await Sut(git).FetchAsync(
            Source(new { repoUrl = "https://github.com/dotnet/aspnetcore" }),
            CancellationToken.None);
        Assert.Contains(result.Documents,
            d => d.UriReference.StartsWith("git://github/dotnet/aspnetcore@main:"));
    }
}
