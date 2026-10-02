using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Ingestion.Staging;

/// <summary>
/// Default implementation of <see cref="IStagingStorageService"/> writing to data/staging/{sourceId}
/// (SPEC-20260924-cloud-storage-connectors RF-002).
/// </summary>
public sealed class StagingStorageService : IStagingStorageService
{
    private readonly string _baseStagingPath;
    private readonly ILogger<StagingStorageService> _logger;

    public StagingStorageService(IConfiguration config, ILogger<StagingStorageService> logger)
    {
        _logger = logger;
        var custom = config["Staging:Path"];
        _baseStagingPath = !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Join(AppContext.BaseDirectory, "data", "staging");
    }

    public string GetStagingDirectory(Guid sourceId)
    {
        var path = Path.Join(_baseStagingPath, sourceId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public Task CleanupStagingAsync(Guid sourceId, CancellationToken ct = default)
    {
        try
        {
            var path = Path.Join(_baseStagingPath, sourceId.ToString("N"));
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                _logger.LogInformation("Cleaned up staging directory for source {SourceId} at {Path}", sourceId, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up staging directory for source {SourceId}", sourceId);
        }
        return Task.CompletedTask;
    }

    public Task<int> CleanupOrphanedStagingAsync(IReadOnlySet<Guid> knownSourceIds, CancellationToken ct = default)
    {
        var removed = 0;
        if (!Directory.Exists(_baseStagingPath))
            return Task.FromResult(removed);

        foreach (var dir in Directory.EnumerateDirectories(_baseStagingPath))
        {
            if (ct.IsCancellationRequested)
                break;
            var name = Path.GetFileName(dir);
            if (Guid.TryParse(name, out var sourceId) && knownSourceIds.Contains(sourceId))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
                _logger.LogInformation("Removed orphaned staging directory {Dir}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove orphaned staging directory {Dir}", dir);
            }
        }
        return Task.FromResult(removed);
    }
}
