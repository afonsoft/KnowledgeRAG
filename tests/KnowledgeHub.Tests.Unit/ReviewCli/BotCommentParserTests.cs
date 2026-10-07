using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Signals;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class BotCommentParserTests
{
    private static readonly ReviewOptions Options = new();

    [Theory]
    [InlineData("devin-ai-integration[bot]", true)]
    [InlineData("sonarcloud[bot]", true)]
    [InlineData("sonarcloud", true)]            // default allowlist without suffix
    [InlineData("github-advanced-security", true)]
    [InlineData("coderabbitai[bot]", true)]
    [InlineData("any-random[bot]", true)]       // suffix rule wins over allowlist
    [InlineData("octocat", false)]
    [InlineData("afonsoft", false)]
    public void IsBot_classifies_authors(string author, bool expected) =>
        Assert.Equal(expected, BotCommentParser.IsBot(author, Options));

    [Fact]
    public void IsBot_honors_env_allowlist()
    {
        var options = new ReviewOptions { BotAuthors = ["acme-reviewer"] };
        Assert.True(BotCommentParser.IsBot("acme-reviewer", options));
        Assert.True(BotCommentParser.IsBot("acme-reviewer[bot]", options));
        Assert.False(BotCommentParser.IsBot("acme", options));
    }

    [Fact]
    public void Filter_drops_human_comments()
    {
        var comments = new[]
        {
            new BotCommentSignal("sonarcloud[bot]", "issue", "3 issues", DateTimeOffset.UtcNow),
            new BotCommentSignal("octocat", "issue", "lgtm", DateTimeOffset.UtcNow),
        };
        var kept = BotCommentParser.Filter(comments, Options);
        Assert.Single(kept);
        Assert.Equal("sonarcloud[bot]", kept[0].Author);
    }
}
