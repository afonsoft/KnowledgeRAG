using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// DocumentFile connector (SPEC-20260914-webpage-docfile-connectors RF-002):
/// a local file or directory (recursive). .md/.txt read directly, .pdf via
/// PdfPig, .docx via OpenXML; other extensions are skipped with a warning.
/// </summary>
public sealed partial class DocumentFileConnector(ILogger<DocumentFileConnector> logger) : ISourceConnector
{
    /// <summary>Extensions with a text extractor. Plain-text extensions cover
    /// code and config files (SPEC-20260923-code-aware-chunking).</summary>
    public static readonly System.Collections.Immutable.ImmutableHashSet<string> SupportedExtensions =
        System.Collections.Immutable.ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        ".md", ".txt", ".pdf", ".docx",
        ".csv", // Google Sheets export (SPEC-20260926-ingestion-connector-integrity RF-005)
        ".cs", ".java", ".js", ".ts", ".py", ".go", ".rs", ".sql",
        ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".config");

    /// <summary>Extensions read as raw text (no parser).</summary>
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".csv",
        ".cs", ".java", ".js", ".ts", ".py", ".go", ".rs", ".sql",
        ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".config"
    };

    private const long DefaultMaxFileBytes = 20L * 1024 * 1024;

    public SourceType Type => SourceType.DocumentFile;

    public async Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var path = config.String("path") ?? config.String("filePath"); // legacy key tolerated
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path))
            throw new InvalidOperationException($"DocumentFile source '{source.Name}': path '{path}' does not exist");

        var glob = config.String("glob") ?? "**/*";
        var maxBytes = config.Int("maxFileSizeMB", 20, 1, 512) * 1024L * 1024;
        var matcher = GlobMatcher.Compile(glob);

        var files = File.Exists(path)
            ? [path]
            : EnumerateFiles(path, matcher);

        var documents = new List<RawDocument>();
        var warnings = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > maxBytes)
                {
                    logger.LogWarning("Skipping {File}: size {Bytes} outside 1..{Max} bytes", file, info.Length, maxBytes);
                    warnings.Add($"{Path.GetFileName(file)}: size outside limit");
                    continue;
                }
                if (!SupportedExtensions.Contains(info.Extension))
                {
                    logger.LogWarning("Skipping {File}: extension '{Ext}' not supported", file, info.Extension);
                    warnings.Add($"{Path.GetFileName(file)}: unsupported extension '{info.Extension}'");
                    continue;
                }

                var text = await ExtractTextAsync(file, info.Extension, cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                {
                    logger.LogWarning("Skipping {File}: no extractable text", file);
                    warnings.Add($"{Path.GetFileName(file)}: no extractable text");
                    continue;
                }

                var uri = File.Exists(path)
                    ? Path.GetFileName(file)
                    : Path.GetRelativePath(path, file);
                documents.Add(new RawDocument(uri, Path.GetFileNameWithoutExtension(file), text));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Failed to extract {File} — skipped", file);
                warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return new FetchResult(documents, warnings);
    }

    /// <summary>Extracts text for one file — used by the incremental watcher path too.</summary>
    internal static async Task<string?> ExtractTextAsync(string file, string extension, CancellationToken ct)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".md":
            case ".txt":
                return await File.ReadAllTextAsync(file, ct);
            case { } e when PlainTextExtensions.Contains(e):
                return await File.ReadAllTextAsync(file, ct);
            case ".pdf":
                return await Task.Run(() => ExtractPdf(file), ct);
            case ".docx":
                return await Task.Run(() => ExtractDocx(file), ct);
            default:
                return null;
        }
    }

    private static string? ExtractPdf(string file)
    {
        using var document = PdfDocument.Open(file);
        var sb = new StringBuilder();
        foreach (Page page in document.GetPages())
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
                sb.AppendLine(text).AppendLine();
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? ExtractDocx(string file)
    {
        using var doc = WordprocessingDocument.Open(file, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return null;
        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            sb.AppendLine(paragraph.InnerText);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IEnumerable<string> EnumerateFiles(string root, Func<string, bool> matcher)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.StartsWith('.')))
                continue; // hidden files/dirs excluded
            if (matcher(relative))
                yield return file;
        }
    }

    /// <summary>Minimal glob: `**/*`, `*.ext`, `**/*.ext`, `{a,b}` groups, exact names.</summary>
    public static class GlobMatcher
    {
        public static Func<string, bool> Compile(string glob)
        {
            // {a,b} groups must split before Regex.Escape (braces are not escaped).
            var pattern = glob.Trim();
            var sb = new System.Text.StringBuilder("^");
            var i = 0;
            foreach (var m in Regex.Matches(pattern, @"\{([^}]*)\}",
                         RegexOptions.None, TimeSpan.FromSeconds(1)).Cast<Match>())
            {
                AppendGlobSegment(sb, pattern[i..m.Index]);
                sb.Append('(');
                sb.Append(string.Join('|', m.Groups[1].Value.Split(',').Select(Regex.Escape)));
                sb.Append(')');
                i = m.Index + m.Length;
            }
            AppendGlobSegment(sb, pattern[i..]);
            sb.Append('$');
            var regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return path => regex.IsMatch(path.Replace('\\', '/'));

            static void AppendGlobSegment(System.Text.StringBuilder sb, string literal)
                => sb.Append(Regex.Escape(literal)
                    .Replace("\\*\\*/", "(.*/)?")   // **/  → any depth
                    .Replace("\\*\\*", ".*")        // **   → anything
                    .Replace("\\*", "[^/]*")        // *    → segment
                    .Replace("\\?", "."));
        }
    }
}
