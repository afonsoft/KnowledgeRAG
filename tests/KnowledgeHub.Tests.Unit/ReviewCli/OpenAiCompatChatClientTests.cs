using KnowledgeHub.Review.Review;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class OpenAiCompatChatClientTests
{
    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com/")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/")]
    [InlineData("http://localhost:11434", "http://localhost:11434/")]
    public void BaseAddress_normalizes_endpoint_with_trailing_slash(string endpoint, string expected)
    {
        using var http = new HttpClient();
        using var client = new OpenAiCompatChatClient(http, endpoint, "gpt-test", null);

        Assert.Equal(expected, http.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public void Bearer_token_sets_authorization_header()
    {
        using var http = new HttpClient();
        using var client = new OpenAiCompatChatClient(http, "https://api.openai.com", "gpt-test", "sk-test");

        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("sk-test", http.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public void Null_api_key_leaves_authorization_header_unset()
    {
        using var http = new HttpClient();
        using var client = new OpenAiCompatChatClient(http, "https://api.openai.com", "gpt-test", null);

        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_endpoint_throws(string? endpoint)
    {
        using var http = new HttpClient();
        Assert.ThrowsAny<ArgumentException>(() => new OpenAiCompatChatClient(http, endpoint!, "gpt-test", null));
    }
}
