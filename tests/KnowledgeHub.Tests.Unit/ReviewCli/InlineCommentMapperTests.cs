using KnowledgeHub.Review.GitHub;
using KnowledgeHub.Review.Output;
using KnowledgeHub.Review.Review;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class InlineCommentMapperTests
{
    private static Finding At(string file, int line, string kind = "bug", string severity = "severe") =>
        new(kind, severity, file, line, null, "issue here", "fix it");

    [Fact]
    public void Added_line_maps_to_comment_with_position()
    {
        var signal = TestSignals.Signal(); // SamplePatch adds "new call" at new line 10
        var comments = InlineCommentMapper.Map([At("src/Foo.cs", 10)], signal);
        var c = Assert.Single(comments);
        Assert.Equal("src/Foo.cs", c.Path);
        Assert.Equal(10, c.Line);
        Assert.Equal("RIGHT", c.Side);
        Assert.True(c.Position > 0);
        Assert.Contains("bug/severe", c.Body);
    }

    [Fact]
    public void Context_line_maps()
    {
        var signal = TestSignals.Signal(); // "ctx forty-one" is context at new line 41
        var comments = InlineCommentMapper.Map([At("src/Foo.cs", 41)], signal);
        Assert.Single(comments);
    }

    [Fact]
    public void Second_hunk_addition_maps()
    {
        var signal = TestSignals.Signal(); // "added line" at new line 42
        var comments = InlineCommentMapper.Map([At("src/Foo.cs", 42)], signal);
        Assert.Single(comments);
    }

    [Fact]
    public void Line_outside_diff_is_dropped()
    {
        var signal = TestSignals.Signal();
        Assert.Empty(InlineCommentMapper.Map([At("src/Foo.cs", 999)], signal));
    }

    [Fact]
    public void Line_between_hunks_is_not_commentable()
    {
        // Line 20 exists in the file but lies between the two hunks — not in diff.
        var signal = TestSignals.Signal();
        Assert.Empty(InlineCommentMapper.Map([At("src/Foo.cs", 20)], signal));
    }

    [Fact]
    public void Finding_on_unknown_file_is_dropped()
    {
        var signal = TestSignals.Signal();
        Assert.Empty(InlineCommentMapper.Map([At("src/NotHere.cs", 11)], signal));
    }

    [Fact]
    public void Repo_level_finding_never_inlines()
    {
        var signal = TestSignals.Signal();
        Assert.Empty(InlineCommentMapper.Map([new Finding("flag", "investigate", null, null, null, "x", null)], signal));
    }

    [Fact]
    public void Position_counts_every_diff_line_after_first_hunk_header()
    {
        const string patch = """
            @@ -1,2 +1,3 @@
             a
            +b
             c
            @@ -10,2 +11,3 @@
             x
            +y
             z
            """;
        // patch lines: 0=@@ hdr, 1=ctx a(new 1), 2=+b(new 2), 3=ctx c(new 3),
        // 4=@@ hdr, 5=ctx x(new 11), 6=+y(new 12), 7=ctx z(new 13)
        Assert.Equal(2, InlineCommentMapper.PositionInPatch(patch, 2));
        Assert.Equal(6, InlineCommentMapper.PositionInPatch(patch, 12));
        Assert.Null(InlineCommentMapper.PositionInPatch(patch, 99));
    }
}
