using System.Text.Json;
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
        var full = ObsidianNoteWriter.SafePath(root, path, forWrite: false,
            ctx.Services.GetService<Security.PathDenyList>());

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
        var full = ObsidianNoteWriter.SafePath(root, path, forWrite: true,
            ctx.Services.GetService<Security.PathDenyList>());

        // SPEC-20261111-memory-defense RF-003: screen before persisting —
        // content plus tags (they land in frontmatter).
        var outcome = Security.WritePathDefense.Screen(
            ctx.Services, db, vault, content, "write_note");
        var tagOutcome = tags is { Length: > 0 }
            ? Security.WritePathDefense.Screen(
                ctx.Services, db, vault, string.Join(',', tags), "write_note")
            : null;
        if (outcome.Blocked || tagOutcome?.Blocked == true)
        {
            var blocked = outcome.Findings
                .Concat(tagOutcome?.Findings ?? [])
                .Where(f => f.Action == Security.SecretAction.Block);
            return await ToolResults.Error(JsonSerializer.Serialize(new
            {
                blocked = true,
                findings = blocked.Select(f => new { pattern = f.Pattern, count = f.Count })
            }));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, ObsidianNoteWriter.WithFrontmatter(
            outcome.Text,
            tagOutcome is null ? tags : tagOutcome.Text.Split(',', StringSplitOptions.TrimEntries),
            ObsidianNoteWriter.ResolveOrigin(ctx.Services)), ct);

        var relative = Path.GetRelativePath(root, full);
        var ingestion = ctx.Services.GetRequiredService<IngestionService>();
        await ingestion.SyncFileAsync(vault.Id, relative, ct);

        var redactions = outcome.Redactions.Count > 0
            ? $" Redacted: {string.Join(',', outcome.Redactions.Select(f => $"{f.Pattern}x{f.Count}"))}."
            : "";
        return await ToolResults.Text($"Wrote `{relative}` to vault '{vault.Name}' and re-indexed it.{redactions}");
    }
}
