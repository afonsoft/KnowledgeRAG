using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260914-hitl-tool-approval: gate suspends the loop on mutating tools,
// approve+resume executes, deny injects a denied result, expiry and 409 semantics.
public class ApprovalsApiTests : IClassFixture<ApprovalsApiTests.Fixture>, IClassFixture<ApprovalsApiTests.ExpiringFixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-appr-{Guid.NewGuid():N}.db");
        public string Vault { get; } = Path.Join(Path.GetTempPath(), $"kh-appr-vault-{Guid.NewGuid():N}");
        public AgentApiTests.ScriptedChatClient Chat { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Vault);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(Chat));
        }
    }

    /// <summary>Same fixture but approvals expire instantly (timeout -1 min).</summary>
    public sealed class ExpiringFixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-appr-exp-{Guid.NewGuid():N}.db");
        public string Vault { get; } = Path.Join(Path.GetTempPath(), $"kh-appr-exp-vault-{Guid.NewGuid():N}");
        public AgentApiTests.ScriptedChatClient Chat { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Vault);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["Agent:ApprovalTimeoutMinutes"] = "-1"
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(Chat));
        }
    }

    private readonly Fixture _factory;
    private readonly ExpiringFixture _expiring;
    private readonly HttpClient _client;

    public ApprovalsApiTests(Fixture factory, ExpiringFixture expiring)
    {
        _factory = factory;
        _expiring = expiring;
        _client = TestAuth.Login(factory);
    }

    private async Task<AgentResponse> StartWriteRunAsync(HttpClient client, string vaultPath)
    {
        // write_knowledge/write_note need an active vault source to land on.
        var create = await client.PostAsJsonAsync("/api/sources", new
        {
            name = $"ApprVault{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = vaultPath },
            isActive = true
        });
        create.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "WRITE_TEST",
            allowWrite = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentResponse>())!;
    }

    [Fact]
    public async Task Gate_SuspendsOnMutatingTool()
    {
        var run = await StartWriteRunAsync(_client, _factory.Vault);

        Assert.NotNull(run.AwaitingApprovalId);
        Assert.Equal("write_knowledge", run.PendingTool);
        Assert.Contains("awaiting approval", run.Answer);
        Assert.Empty(run.Steps); // gated call never executed

        var pending = await _client.GetFromJsonAsync<List<ApprovalDto>>("/api/approvals?status=pending");
        Assert.Contains(pending!, a => a.Id == run.AwaitingApprovalId);
    }

    [Fact]
    public async Task Approve_Resume_ExecutesGatedCall()
    {
        var run = await StartWriteRunAsync(_client, _factory.Vault);
        var id = run.AwaitingApprovalId!.Value;

        var approve = await _client.PostAsJsonAsync($"/api/approvals/{id}/approve", (object?)null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var resume = await _client.PostAsJsonAsync("/api/agent/resume", new { approvalId = id });
        var result = (await resume.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.Equal("agent final answer", result.Answer);
        var step = Assert.Single(result.Steps);
        Assert.Equal("write_knowledge", step.Tool);
        Assert.False(step.IsError);
        Assert.True(File.Exists(Path.Join(_factory.Vault, "agent-note.md"))); // AC: arquivo criado
    }

    [Fact]
    public async Task Deny_ContinuesLoopWithDeniedResult()
    {
        var run = await StartWriteRunAsync(_client, _factory.Vault);
        var id = run.AwaitingApprovalId!.Value;

        var deny = await _client.PostAsync($"/api/approvals/{id}/deny", null);
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);

        // deny returns the continued loop: model sees "denied" and answers without writing.
        var result = (await deny.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.Equal("agent final answer", result.Answer);
        var step = Assert.Single(result.Steps);
        Assert.Equal("write_knowledge", step.Tool);
        Assert.True(step.IsError);
    }

    [Fact]
    public async Task DoubleResolve_Returns409()
    {
        var run = await StartWriteRunAsync(_client, _factory.Vault);
        var id = run.AwaitingApprovalId!.Value;

        await _client.PostAsJsonAsync($"/api/approvals/{id}/approve", (object?)null);
        var second = await _client.PostAsJsonAsync($"/api/approvals/{id}/approve", (object?)null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Resume_OnPending_Returns409()
    {
        var run = await StartWriteRunAsync(_client, _factory.Vault);

        var resume = await _client.PostAsJsonAsync("/api/agent/resume",
            new { approvalId = run.AwaitingApprovalId });
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
    }

    [Fact]
    public async Task Expired_Approval_NeverExecutes()
    {
        var client = await TestAuth.LoginAsync(_expiring);
        var run = await StartWriteRunAsync(client, _expiring.Vault);
        var id = run.AwaitingApprovalId!.Value;

        var approve = await client.PostAsJsonAsync($"/api/approvals/{id}/approve", (object?)null);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);

        var list = await client.GetFromJsonAsync<List<ApprovalDto>>("/api/approvals");
        Assert.Equal("expired", list!.First(a => a.Id == id).Status);
    }
}
