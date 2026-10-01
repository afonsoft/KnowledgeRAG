using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// SonarQube S1075: o scheme 'knowledge://' é uma constante de protocolo MCP (URI interna), não recurso remoto.
#pragma warning disable S1075

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// MCP resources (SPEC-04 RF-004):
/// <c>knowledge://sources</c> — JSON catalog of active sources;
/// <c>obsidian://{slug}/{path}</c> — content of an indexed vault note.
/// </summary>
public static class KnowledgeResourceProvider
{
    public const string CatalogUri = "knowledge://sources";

    public static async Task<ListResourcesResult> ListAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<KnowledgeHubDbContext>();
        var resources = new List<Resource>
        {
            new()
            {
                Uri = CatalogUri,
                Name = "knowledge-sources",
                Title = "Active knowledge sources catalog",
                Description = "JSON catalog of active knowledge sources",
                MimeType = "application/json"
            }
        };

        var vaults = await db.Sources
            .Where(s => s.IsActive && s.SourceType == SourceType.ObsidianVault)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        if (vaults.Count == 0)
            return new ListResourcesResult { Resources = resources };

        var slugs = ToolSlugger.Assign(vaults.Select(v => (v.Id, v.Name)));
        var vaultIds = vaults.Select(v => v.Id).ToHashSet();
        var docs = await db.Documents.AsNoTracking()
            .Where(d => vaultIds.Contains(d.KnowledgeSourceId))
            .Select(d => new { d.KnowledgeSourceId, d.Title, d.UriReference })
            .ToListAsync(ct);

        foreach (var doc in docs)
        {
            resources.Add(new Resource
            {
                Uri = $"obsidian://{slugs[doc.KnowledgeSourceId]}/{doc.UriReference}",
                Name = doc.Title,
                Description = $"Markdown note in vault '{slugs[doc.KnowledgeSourceId]}'",
                MimeType = "text/markdown"
            });
        }
        return new ListResourcesResult { Resources = resources };
    }

    public static async Task<ReadResourceResult> ReadAsync(string uri, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<KnowledgeHubDbContext>();

        if (uri.Equals(CatalogUri, StringComparison.OrdinalIgnoreCase))
        {
            var catalog = await db.Sources.AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name, s.Description, Type = s.SourceType.ToString(), s.LastSyncAt })
                .ToListAsync(ct);
            return Json(uri, JsonSerializer.Serialize(catalog), "application/json");
        }

        if (uri.StartsWith("obsidian://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = uri["obsidian://".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
                throw new McpProtocolException($"malformed resource uri '{uri}'", McpErrorCode.InvalidParams);

            var slug = rest[..slash];
            var docPath = rest[(slash + 1)..];

            var vaults = await db.Sources
                .Where(s => s.IsActive && s.SourceType == SourceType.ObsidianVault)
                .OrderBy(s => s.Name)
                .ToListAsync(ct);
            var slugs = ToolSlugger.Assign(vaults.Select(v => (v.Id, v.Name)));
            var vault = vaults.FirstOrDefault(v => slugs[v.Id] == slug)
                ?? throw new McpProtocolException($"unknown vault slug '{slug}'", McpErrorCode.ResourceNotFound);

            var root = IngestionService.ResolveVaultRoot(vault.ConfigurationJson)
                ?? throw new McpProtocolException("vault has no configured path", McpErrorCode.ResourceNotFound);
            var full = ObsidianNoteWriter.SafePath(root, docPath, forWrite: false);

            if (!File.Exists(full))
                throw new McpProtocolException($"resource '{uri}' not found", McpErrorCode.ResourceNotFound);

            var content = await File.ReadAllTextAsync(full, ct);
            return Text(uri, content, "text/markdown");
        }

        throw new McpProtocolException($"unknown resource uri '{uri}'", McpErrorCode.ResourceNotFound);
    }

    private static ReadResourceResult Text(string uri, string text, string mime) => new()
    {
        Contents = [new TextResourceContents { Uri = uri, Text = text, MimeType = mime }]
    };

    private static ReadResourceResult Json(string uri, string json, string mime) => Text(uri, json, mime);
}
