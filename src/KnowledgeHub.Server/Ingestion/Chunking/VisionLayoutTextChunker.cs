using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260927-ragflow-vision-layout-chunking: layout-aware chunker for
/// markdown/prose. Segments the document into prose runs, table blocks and
/// image lines — tables are atomic (header-preserving splits via
/// <see cref="TableChunkSplitter"/>), images become standalone pieces that can
/// be captioned by a vision-capable chat model (opt-in). Deterministic: line
/// prefix scans only, no regex over block bodies.
/// </summary>
public sealed class VisionLayoutTextChunker(
    IConfiguration? configuration, ChunkKind kind,
    Microsoft.Extensions.AI.IChatClient? chat = null,
    IHttpClientFactory? httpFactory = null,
    ILogger? logger = null) : ITextChunker
{
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ImageFetchTimeout = TimeSpan.FromSeconds(10);

    public ChunkKind Kind => kind;

    public IReadOnlyList<ChunkPiece> Chunk(string text, int maxTokens, int overlapTokens) =>
        ChunkCore(text, maxTokens, overlapTokens, caption: null).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<ChunkPiece>> ChunkAsync(
        string text, int maxTokens, int overlapTokens, CancellationToken ct = default)
    {
        var caption = (configuration?.GetValue("Ingestion:Chunking:EnableVisionCaptioning", false) ?? false)
            && chat is not null && httpFactory is not null
            ? (Func<string, Task<string?>>)(url => CaptionAsync(url, ct))
            : null;
        return await ChunkCore(text, maxTokens, overlapTokens, caption);
    }

    private async Task<IReadOnlyList<ChunkPiece>> ChunkCore(
        string text, int maxTokens, int overlapTokens,
        Func<string, Task<string?>>? caption)
    {
        var preserveHeaders = configuration?.GetValue("Ingestion:Chunking:PreserveTableHeaders", true) ?? true;
        var maxRows = Math.Clamp(
            configuration?.GetValue("Ingestion:Chunking:MaxTableChunkRows", 30) ?? 30, 10, 100);
        var inner = MarkdownTextChunker.For(kind);

        var lines = text.Split('\n');
        var pieces = new List<ChunkPiece>();
        var prose = new StringBuilder();
        var headings = new HeadingTracker();

        void FlushProse()
        {
            if (prose.Length == 0)
                return;
            var block = prose.ToString();
            prose.Clear();
            foreach (var p in inner.Chunk(block, maxTokens, overlapTokens))
                pieces.Add(p);
            // keep heading state current for upcoming table metadata
            foreach (var l in block.Split('\n'))
                headings.Feed(l);
        }

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (TableChunkSplitter.StartsTable(line))
            {
                FlushProse();
                var end = TableChunkSplitter.ReadTableEnd(lines, i);
                var tableLines = lines[i..end];
                var path = headings.CurrentPath;
                foreach (var piece in TableChunkSplitter.Split(tableLines, maxRows, preserveHeaders, path))
                    pieces.Add(piece);
                i = end;
                continue;
            }

            var url = ImageUrl(line);
            if (url is not null)
            {
                FlushProse();
                var block = line.Trim();
                if (caption is not null)
                {
                    var cap = await caption(url);
                    if (cap is not null)
                        block += $"\n[figure caption: {cap}]";
                }
                pieces.Add(new ChunkPiece(block, SectionPath: headings.CurrentPath,
                    MetadataJson: "{\"is_figure\":true}"));
                i++;
                continue;
            }

            prose.Append(line).Append('\n');
            i++;
        }
        FlushProse();
        return pieces;
    }

    /// <summary>Markdown `![alt](url)` / HTML `&lt;img src="url"&gt;` detection —
    /// span-based prefix checks (no regex).</summary>
    internal static string? ImageUrl(string line)
    {
        var t = line.AsSpan().TrimStart();
        if (t.StartsWith("!["))
        {
            var open = t.IndexOf("](");
            var close = open >= 0 ? t.IndexOf(')') : -1;
            if (open > 0 && close > open)
                return t.Slice(open + 2, close - open - 2).ToString();
            return null;
        }
        if (t.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
        {
            var srcIdx = t.IndexOf("src=\"", StringComparison.OrdinalIgnoreCase);
            if (srcIdx < 0) srcIdx = t.IndexOf("src='", StringComparison.OrdinalIgnoreCase);
            if (srcIdx < 0) return null;
            var q = t[srcIdx + 4];
            var from = srcIdx + 5;
            var to = t.Slice(from).IndexOf(q);
            return to > 0 ? t.Slice(from, to).ToString() : null;
        }
        return null;
    }

    private async Task<string?> CaptionAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;
        try
        {
            var http = httpFactory!.CreateClient("vision-caption");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ImageFetchTimeout);
            var bytes = await http.GetByteArrayAsync(uri, cts.Token);
            if (bytes.Length is 0 or > MaxImageBytes)
                return null;
            var mediaType = uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            var response = await chat!.GetResponseAsync(
                [new ChatMessage(ChatRole.User,
                [
                    new TextContent("Describe this image in one sentence for a knowledge-base index."),
                    new DataContent(bytes, mediaType)
                ])],
                cancellationToken: ct);
            var text = response.Text?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "vision caption failed for {Url}", uri);
            return null;
        }
    }

    /// <summary>Tracks the markdown heading stack so table/figure pieces inherit
    /// the current section path (mirrors MarkdownTextChunker semantics).</summary>
    private sealed class HeadingTracker
    {
        private readonly List<(int Level, string Title)> _stack = [];

        public string? CurrentPath =>
            _stack.Count > 0 ? string.Join(" > ", _stack.Select(s => s.Title)) : null;

        public void Feed(string line)
        {
            var t = line.AsSpan().TrimStart();
            if (t.IsEmpty || t[0] != '#')
                return;
            var level = 0;
            while (level < t.Length && level < 6 && t[level] == '#')
                level++;
            if (level == 0 || level >= t.Length || t[level] != ' ')
                return;
            var title = t.Slice(level).Trim().ToString();
            if (title.Length == 0)
                return;
            while (_stack.Count > 0 && _stack[^1].Level >= level)
                _stack.RemoveAt(_stack.Count - 1);
            _stack.Add((level, title));
        }
    }
}
