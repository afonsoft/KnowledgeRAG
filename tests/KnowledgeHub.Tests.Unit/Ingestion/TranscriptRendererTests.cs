using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-youtube-transcript-connector RF-006: transcript
// rendering with/without timestamps, HTML tag stripping, dedup of
// consecutive repetitions, and document header formatting.
public class TranscriptRendererTests
{
    private static readonly VideoInfo SampleVideo = new(
        "dQw4w9WgXcQ", "My Talk Title", "Channel Name", TimeSpan.FromMinutes(5));

    private static List<Caption> Captions(params (string text, int seconds)[] items) =>
        items.Select(i => new Caption(i.text, TimeSpan.FromSeconds(i.seconds))).ToList();

    [Fact]
    public void Render_WithoutTimestamps_ProducesParagraphs()
    {
        var captions = Captions(("Hello world", 0), ("Goodbye world", 2));
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: false);

        Assert.Contains("# My Talk Title", text);
        Assert.Contains("Canal: Channel Name", text);
        Assert.Contains("URL: https://youtu.be/dQw4w9WgXcQ", text);
        Assert.Contains("Duração: 05:00", text);
        Assert.Contains("Hello world", text);
        Assert.Contains("Goodbye world", text);
        Assert.DoesNotContain("[00:00]", text);
    }

    [Fact]
    public void Render_WithTimestamps_ProducesTimestampedLines()
    {
        var captions = Captions(("Hello world", 0), ("Goodbye", 65));
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: true);

        Assert.Contains("[00:00] Hello world", text);
        Assert.Contains("[01:05] Goodbye", text);
    }

    [Fact]
    public void Render_StripsHtmlTags()
    {
        var captions = Captions(("<i>Hello</i> <b>world</b>", 0));
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: false);

        Assert.DoesNotContain("<i>", text);
        Assert.DoesNotContain("<b>", text);
        Assert.Contains("Hello world", text);
    }

    [Fact]
    public void Render_StripsHtmlEntities()
    {
        var captions = Captions(("Hello &amp; goodbye &#39;sir&#39;", 0));
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: false);

        Assert.DoesNotContain("&amp;", text);
        Assert.DoesNotContain("&#39;", text);
        Assert.Contains("Hello & goodbye 'sir'", text);
    }

    [Fact]
    public void Render_DedupConsecutiveRepetitions()
    {
        // Auto-captions repeat the previous line when there's a pause.
        var captions = Captions(
            ("Hello world", 0),
            ("Hello world", 1),
            ("Hello world", 2),
            ("Next line", 3));
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: false);

        // "Hello world" should appear once in the body (deduped), then "Next line"
        var bodyStart = text.IndexOf("\n\n");
        var body = bodyStart >= 0 ? text[(bodyStart + 2)..] : text;
        Assert.Equal("Hello world\nNext line", body.Trim());
    }

    [Fact]
    public void Render_AllowsNonConsecutiveRepetition()
    {
        var captions = Captions(
            ("Alpha", 0),
            ("Beta", 1),
            ("Alpha", 2)); // Same text but not consecutive — keep.
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: false);

        var bodyStart = text.IndexOf("\n\n");
        var body = bodyStart >= 0 ? text[(bodyStart + 2)..] : text;
        Assert.Contains("Alpha", body);
        Assert.Contains("Beta", body);
        // Alpha appears twice (non-consecutive)
        Assert.Equal(2, body.Split("Alpha").Length - 1);
    }

    [Fact]
    public void Render_EmptyCaptions_ReturnsHeaderOnly()
    {
        var text = TranscriptRenderer.Render(SampleVideo, [], includeTimestamps: false);
        Assert.Contains("# My Talk Title", text);
        Assert.Contains("Canal: Channel Name", text);
        // Body should be empty after the header
        var bodyStart = text.IndexOf("\n\n");
        if (bodyStart >= 0)
        {
            var body = text[(bodyStart + 2)..].Trim();
            Assert.Empty(body);
        }
    }

    [Fact]
    public void Render_Timestamp_FormatIsMinutesSeconds()
    {
        var captions = Captions(("Test", 0), ("Test2", 3661)); // 1h 1m 1s
        var text = TranscriptRenderer.Render(SampleVideo, captions, includeTimestamps: true);

        Assert.Contains("[00:00] Test", text);
        Assert.Contains("[01:01:01] Test2", text);
    }
}
