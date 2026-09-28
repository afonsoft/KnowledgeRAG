using System.Text.Json;
using KnowledgeHub.Server.Ingestion.Chunking;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Ingestion.Chunking;

// Covers SPEC-20260927-ragflow-vision-layout-chunking: atomic table blocks,
// header-preserving row splits, continuation markers, structural metadata and
// figure isolation.
public sealed class VisionLayoutChunkerTests
{
    private static string Table(int rows) =>
        "| Col A | Col B |\n" +
        "|---|---|\n" +
        string.Join('\n', Enumerable.Range(1, rows).Select(i => $"| a{i} | b{i} |"));

    private static VisionLayoutTextChunker Chunker(int maxRows = 30, bool headers = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ingestion:Chunking:MaxTableChunkRows"] = maxRows.ToString(),
            ["Ingestion:Chunking:PreserveTableHeaders"] = headers ? "true" : "false"
        }).Build(),
            ChunkKind.Markdown);

    [Fact]
    public void SmallTable_StaysAtomic_SingleChunk()
    {
        // AC: 10-row table within the limit → one chunk, no splits.
        var pieces = Chunker().Chunk($"Intro text.\n\n{Table(10)}\n\nOutro.", 500, 50);

        var table = pieces.Where(p => p.MetadataJson is not null).ToList();
        var single = Assert.Single(table);
        Assert.Contains("| a10 | b10 |", single.Text);
        Assert.Contains("| Col A | Col B |", single.Text);
        Assert.DoesNotContain("Continuação", single.Text);
    }

    [Fact]
    public void LongTable_SplitsWithHeaderRepetition()
    {
        // AC: 60-row table, limit 25 → each slice keeps the original header.
        var pieces = Chunker(maxRows: 25).Chunk(Table(60), 100000, 0);

        Assert.Equal(3, pieces.Count); // 25 + 25 + 10
        foreach (var (piece, idx) in pieces.Select((p, i) => (p, i)))
        {
            Assert.Contains("| Col A | Col B |", piece.Text);
            Assert.Contains("|---|", piece.Text);
            if (idx > 0)
                Assert.Contains("[Continuação da Tabela", piece.Text);
        }
        Assert.Equal(60, pieces.Sum(p => p.Text.Split('\n')
            .Count(l => l.StartsWith("| a"))));
    }

    [Fact]
    public void Table_IsIsolatedFromAdjacentProse()
    {
        // RF-001: table never interleaved with neighbouring paragraphs.
        var pieces = Chunker().Chunk($"Before paragraph.\n\n{Table(3)}\n\nAfter paragraph.", 500, 50);

        var tableIdx = pieces.ToList().FindIndex(p => p.MetadataJson is not null);
        Assert.True(tableIdx > 0 && tableIdx < pieces.Count - 1);
        Assert.DoesNotContain("Before paragraph", pieces[tableIdx].Text);
        Assert.DoesNotContain("After paragraph", pieces[tableIdx].Text);
    }

    [Fact]
    public void Table_Metadata_CarriesHeadersAndSection()
    {
        // RF-003: is_table, table_headers, rows, columns, parent_section_title.
        var text = $"## Pricing\n\n{Table(3)}";
        var pieces = Chunker().Chunk(text, 500, 50);

        var table = Assert.Single(pieces, p => p.MetadataJson is not null);
        using var meta = JsonDocument.Parse(table.MetadataJson!);
        var root = meta.RootElement;
        Assert.True(root.GetProperty("is_table").GetBoolean());
        Assert.Equal(3, root.GetProperty("table_rows_count").GetInt32());
        Assert.Equal(2, root.GetProperty("table_columns").GetInt32());
        Assert.Equal(["Col A", "Col B"],
            root.GetProperty("table_headers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Pricing", root.GetProperty("parent_section_title").GetString());
        Assert.Equal("Pricing", table.SectionPath);
    }

    [Fact]
    public void MalformedTable_NoSeparator_TreatedAsAtomic()
    {
        // `|` block without the `|---|` separator — first line is the header.
        var text = "| just | rows |\n| a | b |\n| c | d |";
        var pieces = Chunker().Chunk(text, 500, 50);

        var table = Assert.Single(pieces);
        Assert.Contains("| c | d |", table.Text);
    }

    [Fact]
    public void Image_BecomesOwnPiece_WithFigureMetadata()
    {
        var text = "Some text.\n\n![diagram](https://example.com/d.png)\n\nMore text.";
        var pieces = Chunker().Chunk(text, 500, 50);

        var figure = Assert.Single(pieces, p => p.MetadataJson?.Contains("is_figure") == true);
        Assert.Contains("![diagram]", figure.Text);
    }

    [Fact]
    public void HtmlTable_Detected_AsAtomicBlock()
    {
        var text = "Lead-in.\n\n<table>\n<tr><th>A</th></tr>\n<tr><td>1</td></tr>\n</table>\n\nTail.";
        var pieces = Chunker().Chunk(text, 500, 50);

        var table = Assert.Single(pieces, p => p.MetadataJson is not null);
        Assert.Contains("</table>", table.Text);
    }
}
