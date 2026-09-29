using System.Net;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Security;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260929-connector-security-sync-safety:
// RF-001 (inline secret accepted on create), RF-005 (egress policy — metadata
// IP block + cross-host redirect credential strip), RF-007 (GitLab subgroups).
public sealed class ConnectorSecurityTests
{
    // ---------- RF-001: new keys are write-through on create ----------

    [Fact]
    public async Task Create_RestApi_InlineHeaders_NotRejected()
    {
        // Bug: hasKey=true + inline headers was rejected because validation
        // consulted the (empty) secret store before persistence.
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var secrets = new McpProxySourceServiceTests.FakeSecretStore();
        var svc = new KnowledgeSourceService(db, new FakeNotifier(), secrets);

        var config = new JsonObject
        {
            ["endpoint"] = "https://api.example.com/items",
            ["headers"] = """{"Authorization":"Bearer abc123"}""",
            ["hasKey"] = true
        };
        var result = await svc.CreateAsync(new CreateKnowledgeSourceRequest
        {
            Name = "api",
            Type = SourceType.RestApi,
            Configuration = config
        });

        Assert.Null(result.Error);
        Assert.NotNull(result.Value);
    }

    // ---------- RF-007: GitLab subgroup owner ----------

    [Fact]
    public async Task GitRepo_SubgroupUrl_OwnerIsFullPath()
    {
        var config = ConnectorConfig.Parse(
            """{"repoUrl":"https://gitlab.com/group/subgroup/repo.git","allowPrivateHosts":false}""");

        var repo = await GitRepositoryConnector.ResolveRepoAsync(config, CancellationToken.None);

        Assert.Equal("group/subgroup", repo.Owner);
        Assert.Equal("repo", repo.Name);
        Assert.Equal("gitlab", repo.Provider);
    }

    // ---------- RF-005: egress policy ----------

    private sealed class FakeInner(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(route(request));
        }
    }

    [Fact]
    public async Task Egress_MetadataIp_Blocked()
    {
        // AC-4: link-local metadata endpoint refused outright.
        var inner = new FakeInner(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new EgressPolicyHandler(allowPrivateNetworks: true) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://169.254.169.254/latest/meta-data"));
        Assert.Empty(inner.Seen);
    }

    [Fact]
    public async Task Egress_CrossHostRedirect_StripsAuthorization()
    {
        // AC-3: 302 to another host → the forwarded request carries no creds.
        var requests = new List<HttpRequestMessage>();
        var inner = new FakeInner(req =>
        {
            requests.Add(req);
            return req.RequestUri!.Host == "api.gitlab.com"
                ? new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://evil.example.com/x") }
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok")
                };
        });
        var handler = new EgressPolicyHandler(allowPrivateNetworks: true) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.gitlab.com/api/v4/projects");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "glpat-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[1].Headers.Authorization); // PAT never leaves origin
    }

    [Fact]
    public async Task Egress_CrossHostRedirect_StripsPrivateTokenAndCustomSecrets()
    {
        // Devin-review: GitLab PRIVATE-TOKEN and RestApi custom secret headers
        // must also be stripped — not just Authorization/X-Api-Key.
        var requests = new List<HttpRequestMessage>();
        var inner = new FakeInner(req =>
        {
            requests.Add(req);
            return req.RequestUri!.Host == "gitlab.internal"
                ? new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://attacker.example/x") }
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var handler = new EgressPolicyHandler(allowPrivateNetworks: true) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gitlab.internal/api/v4");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", "glpat-secret");
        request.Headers.TryAddWithoutValidation("X-Custom-Secret", "s3cr3t");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(requests[1].Headers.Contains("PRIVATE-TOKEN"));
        Assert.False(requests[1].Headers.Contains("X-Custom-Secret"));
        Assert.True(requests[1].Headers.Contains("Accept")); // safe allowlist survives
    }

    [Fact]
    public async Task Egress_HttpsToHttp_SameHost_StripsCredentials()
    {
        // Scheme downgrade leaks creds in cleartext — strip even on same host.
        var requests = new List<HttpRequestMessage>();
        var inner = new FakeInner(req =>
        {
            requests.Add(req);
            return req.RequestUri!.Scheme == "https"
                ? new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("http://cdn.example.com/x") }
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var handler = new EgressPolicyHandler(allowPrivateNetworks: true) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.com/a");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "tok");

        await client.SendAsync(request);

        Assert.Equal("http", requests[1].RequestUri!.Scheme);
        Assert.Null(requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task Egress_NonHttpRedirectTarget_NotFollowed()
    {
        var inner = new FakeInner(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("file:///etc/passwd") }
        });
        var handler = new EgressPolicyHandler(allowPrivateNetworks: true) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://api.example.com/x");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode); // surfaced, not followed
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task Egress_PrivateNet_BlockedByDefault_OptInPerRequest()
    {
        // SPEC RF-005: block by default; connector allowPrivateHosts opts in.
        var inner = new FakeInner(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new EgressPolicyHandler { InnerHandler = inner }; // default: deny
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://192.168.1.10/api"));
        Assert.Empty(inner.Seen);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://192.168.1.10/api");
        request.Options.Set(EgressPolicyHandler.AllowPrivateHostsKey, true);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class FakeNotifier : KnowledgeHub.Server.Mcp.IToolCatalogChangeNotifier
    {
        public long Version { get; private set; }
        public Task NotifyToolsChangedAsync(CancellationToken ct = default)
        { Version++; return Task.CompletedTask; }
    }
}
