using KnowledgeHub.Server.Graph;

namespace KnowledgeHub.Tests.Unit.Graph;

/// <summary>
/// SPEC-20260927-temporal-episodic-knowledge-graph RF-002/RF-003: tolerant
/// timestamp formats, UTC assumption for timezone-less input, and the
/// sliding-window whitelist.
/// </summary>
public sealed class TemporalDateParserTests
{
    [Theory]
    [InlineData("2026-09-27T10:00:00Z", 10, 0, 0)]
    [InlineData("2026-09-27T10:00:00", 10, 0, 0)]
    [InlineData("2026-09-27 10:00:00", 10, 0, 0)]
    [InlineData("2026-09-27", 0, 0, 0)]
    [InlineData("2026-09-27T10:15:30+02:00", 8, 15, 30)]
    public void Parse_SupportedFormats_ConvertToUtc(
        string input, int hour, int minute, int second)
    {
        var value = TemporalDateParser.Parse(input);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(hour, value.Hour);
        Assert.Equal(minute, value.Minute);
        Assert.Equal(second, value.Second);
    }

    [Fact]
    public void Parse_TimezonelessInput_AssumesUtc()
    {
        var value = TemporalDateParser.Parse("2026-09-27T10:00:00");
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), value);
    }

    [Fact]
    public void Parse_DateOnly_IsMidnightUtc()
    {
        var value = TemporalDateParser.Parse("2026-09-27");
        Assert.Equal(new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc), value);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-45")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("27/09/2026 banana")]
    public void TryParse_InvalidInput_ReturnsFalse(string input)
    {
        Assert.False(TemporalDateParser.TryParse(input, out _));
    }

    [Fact]
    public void Parse_InvalidInput_ThrowsClearMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => TemporalDateParser.Parse("42"));
        Assert.Contains("yyyy-MM-dd", ex.Message);
    }

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 6)]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    public void TryParseWindow_WhitelistTokens_Resolve(string window, int expectedHours)
    {
        Assert.True(TemporalDateParser.TryParseWindow(window, out var span));
        Assert.Equal(TimeSpan.FromHours(expectedHours), span);
    }

    [Theory]
    [InlineData("48h")]
    [InlineData("2d")]
    [InlineData("30m")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseWindow_UnsupportedTokens_Rejected(string? window)
    {
        Assert.False(TemporalDateParser.TryParseWindow(window, out _));
    }

    [Fact]
    public void ParseWindow_InvalidValue_ListsPermittedValues()
    {
        var ex = Assert.Throws<ArgumentException>(() => TemporalDateParser.ParseWindow("48h"));
        Assert.Contains("1h", ex.Message);
        Assert.Contains("6h", ex.Message);
        Assert.Contains("24h", ex.Message);
        Assert.Contains("7d", ex.Message);
    }
}
