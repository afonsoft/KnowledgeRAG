using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Ingestion;

/// <summary>
/// First-boot default vault seed: when <c>Vault:Path</c> is configured and no
/// ObsidianVault source exists, registers an active source pointing at that
/// path so <c>write_note</c>/<c>write_knowledge</c>/<c>read_document</c> work
/// out of the box — e.g. the compose named volume mounted at /vaults/default.
/// Runs only while zero ObsidianVault sources exist, so sources created or
/// deleted through the UI are never overwritten; set <c>Vault:Path</c> empty
/// to disable permanently. Never logs secret material.
/// Note: vault-scoped tools resolve their target as the first ACTIVE
/// ObsidianVault source ordered by name (<c>ObsidianNoteWriter.ResolveVaultAsync</c>)
/// — the seeded vault becomes the default write target when it sorts first;
/// callers can always pin another vault via the <c>source</c> parameter.
/// </summary>
public static class VaultSeeder
{
    public const string DefaultName = "Default Vault";

    public static async Task SeedAsync(
        KnowledgeHubDbContext db,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var path = configuration["Vault:Path"];
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = configuration["Vault:Name"];
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultName;

        if (await db.Sources.AnyAsync(s => s.SourceType == SourceType.ObsidianVault, cancellationToken))
            return;

        // Sources.Name has a unique index across ALL types — a pre-existing
        // non-vault source with this name would make SaveChanges throw and
        // abort startup. Pick the next free "Name (n)" instead.
        var baseName = name;
        var suffix = 1;
        while (await db.Sources.AnyAsync(s => s.Name == name, cancellationToken))
            name = $"{baseName} ({++suffix})";

        if (!Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex,
                    "Vault:Path '{Path}' does not exist and could not be created — skipping default vault seed.", path);
                return;
            }
        }

        db.Sources.Add(new KnowledgeSource
        {
            Name = name,
            Description = "Seeded from Vault:Path — the container's default Obsidian vault.",
            SourceType = SourceType.ObsidianVault,
            ConfigurationJson = new JsonObject { ["path"] = path }.ToJsonString(),
            IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded default Obsidian vault source '{Name}' at {Path}.", name, path);
    }
}
