using KnowledgeHub.Server.Context;
using ModelContextProtocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Context;

/// <summary>SPEC-20261004-context-snapshots RF-001 — repo normalization.</summary>
public class RepoScopeTests
{
    [Theory]
    [InlineData("afonsoft/knowledgerag", "afonsoft", "knowledgerag")]
    [InlineData("  afonsoft/knowledgerag  ", "afonsoft", "knowledgerag")] // trim
    [InlineData("Afonsoft/KnowledgeRAG", "afonsoft", "knowledgerag")] // lowercase
    [InlineData("owner.name/repo_name-x.y", "owner.name", "repo_name-x.y")]
    [InlineData("https://github.com/Afonsoft/KnowledgeRAG.git", "afonsoft", "knowledgerag")]
    [InlineData("https://github.com/afonsoft/knowledgerag", "afonsoft", "knowledgerag")]
    [InlineData("http://github.com/afonsoft/knowledgerag/", "afonsoft", "knowledgerag")]
    public void Parse_ValidInputs_Normalize(string raw, string owner, string name)
    {
        var scope = RepoScope.Parse(raw);
        Assert.Equal(owner, scope.Owner);
        Assert.Equal(name, scope.Name);
        Assert.Equal($"{owner}/{name}", scope.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("knowledgerag")] // sem barra
    [InlineData("a/b/c")] // muitas barras
    [InlineData("/knowledgerag")] // owner vazio
    [InlineData("afonsoft/")] // name vazio
    [InlineData("afon soft/knowledgerag")] // espaço interno
    [InlineData("afonsoft/repo!")] // caractere fora do conjunto
    [InlineData("https://gitlab.com/a/b")] // host não-github não é aceito como URL
    public void Parse_InvalidInputs_Throw(string? raw)
    {
        var ex = Assert.Throws<McpProtocolException>(() => RepoScope.Parse(raw));
        Assert.Contains("invalid_repo", ex.Message);
    }

    [Fact]
    public void Parse_SegmentTooLong_Throws()
    {
        var longOwner = new string('a', 101);
        Assert.Throws<McpProtocolException>(() => RepoScope.Parse($"{longOwner}/repo"));
    }

    [Fact]
    public void TryParse_MatchesParse()
    {
        Assert.True(RepoScope.TryParse("a/b", out var s));
        Assert.Equal("a/b", s.Key);
        Assert.False(RepoScope.TryParse("bad", out _));
    }
}
