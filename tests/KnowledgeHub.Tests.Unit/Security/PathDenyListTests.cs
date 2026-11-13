using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Security;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Security;

/// <summary>SPEC-20261114-path-denylist: name-pattern deny-list — defaults,
/// segment vs filename matching, allow-list precedence, SafePath wiring.</summary>
public class PathDenyListTests
{
    private static PathDenyList Deny(IDictionary<string, string?>? kv = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            kv ?? new Dictionary<string, string?>()).Build());

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("prod.pem")]
    [InlineData("server.key")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519_backup")]
    [InlineData("cluster.kubeconfig")]
    [InlineData("credentials.md")]
    [InlineData("secrets.md")]
    [InlineData("api-keys.md")]
    [InlineData("tokens.txt")]
    public void Denies_Sensitive_Names(string path) =>
        Assert.True(Deny().IsDenied(path), path);

    [Theory]
    [InlineData(".ssh/keys.md")]
    [InlineData("home/.aws/credentials.md")]
    [InlineData("notes/credentials.md")]
    [InlineData("project/secrets/api.md")]
    public void Denies_Sensitive_Segments(string path) =>
        Assert.True(Deny().IsDenied(path), path);

    [Theory]
    [InlineData("journal/nota.md")]
    [InlineData("architecture/decisions.md")]
    [InlineData("my-secrets-keepass-review.md")] // "*-secrets" exige terminar em -secrets
    [InlineData("tokenizer.md")]
    public void Allows_Normal_Paths(string path) =>
        Assert.False(Deny().IsDenied(path), path);

    [Fact]
    public void AllowList_Wins_Over_Deny()
    {
        var deny = Deny(new Dictionary<string, string?>
        {
            ["Security:PathAllowList:0"] = "credentials.md"
        });
        Assert.False(deny.IsDenied("credentials.md"));
        Assert.True(deny.IsDenied("secrets.md")); // allow é match exato — outros continuam denied
    }

    [Fact]
    public void Extra_Deny_Patterns_From_Config()
    {
        var deny = Deny(new Dictionary<string, string?>
        {
            ["Security:PathDenyList:0"] = "*.draft"
        });
        Assert.True(deny.IsDenied("wip.draft"));
        Assert.False(deny.IsDenied("wip.md"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_Or_Empty_Not_Denied(string? path) =>
        Assert.False(Deny().IsDenied(path));

    // ---- SafePath wiring (RF-002) ----

    [Fact]
    public void SafePath_Denies_Sensitive_Name_Inside_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<McpProtocolException>(() =>
            ObsidianNoteWriter.SafePath(root, "credentials.md", forWrite: false, Deny()));
        Assert.Contains("denied by policy", ex.Message);
        Assert.DoesNotContain("credentials*", ex.Message); // não revela o pattern
    }

    [Fact]
    public void SafePath_Allows_Normal_Name()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var full = ObsidianNoteWriter.SafePath(root, "journal/nota.md", forWrite: true, Deny());
        Assert.EndsWith("nota.md", full);
    }
}
