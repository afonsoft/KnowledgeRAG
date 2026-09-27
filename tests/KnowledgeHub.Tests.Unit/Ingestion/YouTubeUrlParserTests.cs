using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-youtube-transcript-connector RF-002: URL/ID
// classification into Video, Playlist or Channel across all accepted formats.
public class YouTubeUrlParserTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc123", "dQw4w9WgXcQ")]
    public void Video_Urls_ParseCorrectly(string input, string expectedId)
    {
        var entry = YouTubeUrlParser.Parse(input);
        Assert.NotNull(entry);
        Assert.Equal(YouTubeEntryKind.Video, entry!.Kind);
        Assert.Equal(expectedId, entry.Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLrAXtmRdnEQy6nuuH5XZp9Z5v2S2tZ6aA", "PLrAXtmRdnEQy6nuuH5XZp9Z5v2S2tZ6aA")]
    [InlineData("https://youtube.com/playlist?list=UU1234567890", "UU1234567890")]
    [InlineData("PLrAXtmRdnEQy6nuuH5XZp9Z5v2S2tZ6aA", "PLrAXtmRdnEQy6nuuH5XZp9Z5v2S2tZ6aA")]
    [InlineData("OLAK5uy_k12345", "OLAK5uy_k12345")]
    public void Playlist_Urls_ParseCorrectly(string input, string expectedId)
    {
        var entry = YouTubeUrlParser.Parse(input);
        Assert.NotNull(entry);
        Assert.Equal(YouTubeEntryKind.Playlist, entry!.Kind);
        Assert.Equal(expectedId, entry.Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/channel/UCxxxxxxxxxxxxxxxxxxxx", "UCxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("UCxxxxxxxxxxxxxxxxxxxx", "UCxxxxxxxxxxxxxxxxxxxx")]
    public void Channel_Id_Urls_ParseCorrectly(string input, string expectedId)
    {
        var entry = YouTubeUrlParser.Parse(input);
        Assert.NotNull(entry);
        Assert.Equal(YouTubeEntryKind.Channel, entry!.Kind);
        Assert.Equal(expectedId, entry!.Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/@somechannel", "@somechannel")]
    [InlineData("@somechannel", "@somechannel")]
    public void Channel_Handle_Urls_ParseCorrectly(string input, string expectedId)
    {
        var entry = YouTubeUrlParser.Parse(input);
        Assert.NotNull(entry);
        Assert.Equal(YouTubeEntryKind.Channel, entry!.Kind);
        Assert.Equal(expectedId, entry!.Id);
    }

    [Theory]
    [InlineData("notaurl")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/video")]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    public void Invalid_Urls_ReturnNull(string input)
    {
        Assert.Null(YouTubeUrlParser.Parse(input));
    }

    [Fact]
    public void Watch_Url_Without_V_Param_ReturnsNull()
    {
        Assert.Null(YouTubeUrlParser.Parse("https://www.youtube.com/watch"));
    }

    [Fact]
    public void ShortVideoId_ReturnsNull()
    {
        // Too short to be a valid video ID (11 chars required)
        Assert.Null(YouTubeUrlParser.Parse("shortid"));
    }
}
