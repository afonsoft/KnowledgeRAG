using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Obsidian vault tools — present only when ≥1 active ObsidianVault source exists
/// (SPEC-04 RF-001/RF-003): read_document, write_note.
/// </summary>
public sealed class ObsidianToolsProvider : IToolProvider
{
    private static readonly JsonObject ReadSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "path":{"type":"string","description":"Vault-relative path of the document","examples":["folder/note.md"]},
          "source":{"type":"string","description":"Vault slug (default: first active vault)"}
        },"required":["path"],
        "examples":[{"path":"folder/note.md"}]}
        """)!.AsObject();

    private static readonly JsonObject WriteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "path":{"type":"string","description":"Vault-relative note path (.md is appended when missing)","examples":["journal/2026-09-14"]},
          "content":{"type":"string","description":"Markdown content","examples":["# Note\n\nText."]},
          "tags":{"type":"array","items":{"type":"string"},"description":"Tags → frontmatter","examples":[["journal"]]},
          "source":{"type":"string","description":"Vault slug (default: first active vault)"}
        },"required":["path","content"],
        "examples":[{"path":"journal/2026-09-14","content":"# Note\n\nText.","tags":["journal"]}]}
        """)!.AsObject();

    public async Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<KnowledgeHubDbContext>();
        var hasVault = await db.Sources
            .AnyAsync(s => s.IsActive && s.SourceType == SourceType.ObsidianVault, cancellationToken);
        if (!hasVault)
            return [];

        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "read_document",
                Title = "Read document",
                Description = "Reads the full content of a markdown document from an active vault source, addressed by its vault-relative path.",
                InputSchema = ReadSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ReadDocumentAsync
            },
            new CatalogTool
            {
                Name = "write_note",
                Title = "Write note",
                Description = "Writes a markdown note into an active vault source and re-indexes it. Appends .md when missing. Fails on read-only vaults.",
                InputSchema = WriteSchema,
                DestructiveHint = true,
                Handler = WriteNoteAsync
            }
        ];
        return tools;
    }

    private static async ValueTask<CallToolResult> ReadDocumentAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.RequiredString(ctx, "path");
        var sourceSlug = ToolArgs.OptionalString(ctx, "source");

        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var vault = await ObsidianNoteWriter.ResolveVaultAsync(db, sourceSlug, ct)
            ?? throw new McpProtocolException(
                sourceSlug is null ? "no active ObsidianVault source" : $"unknown vault slug '{sourceSlug}'",
                McpErrorCode.InvalidParams);

        var root = IngestionService.ResolveVaultRoot(vault.ConfigurationJson)
            ?? throw new McpProtocolException("vault source has no configured path", McpErrorCode.InvalidParams);
        var full = ObsidianNoteWriter.SafePath(root, path, forWrite: false);

        if (!File.Exists(full))
            return await ToolResults.Error($"document '{path}' not found in vault '{vault.Name}'");

        var content = await File.ReadAllTextAsync(full, ct);
        return await ToolResults.Text(content);
    }

    private static async ValueTask<CallToolResult> WriteNoteAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.RequiredString(ctx, "path");
        var content = ToolArgs.RequiredString(ctx, "content");
        var tags = ToolArgs.OptionalStringArray(ctx, "tags");
        var sourceSlug = ToolArgs.OptionalString(ctx, "source");

        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var vault = await ObsidianNoteWriter.ResolveVaultAsync(db, sourceSlug, ct)
            ?? throw new McpProtocolException(
                sourceSlug is null ? "no active ObsidianVault source" : $"unknown vault slug '{sourceSlug}'",
                McpErrorCode.InvalidParams);

        if (ObsidianNoteWriter.IsReadOnly(vault))
            return await ToolResults.Error($"vault '{vault.Name}' is read-only");

        var root = IngestionService.ResolveVaultRoot(vault.ConfigurationJson)
            ?? throw new McpProtocolException("vault source has no configured path", McpErrorCode.InvalidParams);
        var full = ObsidianNoteWriter.SafePath(root, path, forWrite: true);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, ObsidianNoteWriter.WithFrontmatter(
            content, tags, ObsidianNoteWriter.ResolveOrigin(ctx.Services)), ct);

        var relative = Path.GetRelativePath(root, full);
        var ingestion = ctx.Services.GetRequiredService<IngestionService>();
        await ingestion.SyncFileAsync(vault.Id, relative, ct);

        return await ToolResults.Text($"Wrote `{relative}` to vault '{vault.Name}' and re-indexed it.");
    }
}
