using KnowledgeHub.Server.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory.MentalModels;

/// <summary>
/// SPEC-20261111-mental-models-pages RF-003: projects subtype='page' models
/// into the Obsidian vault as markdown under
/// <c>{vault}/_memory/{bankKey}/{path}/{name}.md</c>. The <c>_memory/</c>
/// folder is hub-managed (banner comment); projection is best-effort — vault
/// failures never fail a refresh.
/// </summary>
public sealed class VaultPageProjector(
    IConfiguration config,
    ILogger<VaultPageProjector> logger)
{
    public const string ManagedBanner = "<!-- managed-by:knowledge-hub -->";

    private string? VaultRoot =>
        config["Vault:Path"] is { Length: > 0 } p ? p : null;

    public bool Enabled =>
        config.GetValue("Memory:Pages:ProjectToVault", false) && VaultRoot is not null;

    public string FilePathFor(string bankKey, string path, string name)
    {
        var dir = Path.Combine(VaultRoot!, "_memory", bankKey);
        if (path.Length > 0)
            dir = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(dir, $"{Slug(name)}.md");
    }

    public async Task ProjectAsync(MemoryMentalModel model, string bankKey, CancellationToken ct)
    {
        if (!Enabled || model.Subtype != "page")
            return;
        try
        {
            var file = FilePathFor(bankKey, model.Path, model.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var md = $"""
                {ManagedBanner}
                ---
                bank: {bankKey}
                page: {model.Name}
                path: {model.Path}
                source_query: "{model.SourceQuery.Replace('"', '\'')}"
                refreshed_at: {model.LastRefreshedAt:O}
                ---

                {model.Content}
                """;
            await File.WriteAllTextAsync(file, md, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Page projection failed for model {ModelId}", model.Id);
        }
    }

    /// <summary>Delete the projected file.</summary>
    public Task RemoveAsync(MemoryMentalModel model, string bankKey, CancellationToken ct)
    {
        if (!Enabled || model.Subtype != "page")
            return Task.CompletedTask;
        try
        {
            var file = FilePathFor(bankKey, model.Path, model.Name);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Page projection cleanup failed for model {ModelId}", model.Id);
        }
        return Task.CompletedTask;
    }

    /// <summary>Path/name change → delete old file, write new one.</summary>
    public async Task MoveAsync(
        MemoryMentalModel model, string bankKey, string oldPath, string oldName, CancellationToken ct)
    {
        if (!Enabled || model.Subtype != "page")
            return;
        try
        {
            var file = FilePathFor(bankKey, oldPath, oldName);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Page projection move cleanup failed for model {ModelId}", model.Id);
        }
        await ProjectAsync(model, bankKey, ct);
    }

    public static string Slug(string name) =>
        string.Concat(name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-'))
            .Trim('-');
}
