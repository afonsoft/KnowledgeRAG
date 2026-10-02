using KnowledgeHub.Server.Ingestion.Staging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server.Ingestion;

// Covers SPEC-20260924-cloud-storage-connectors RF-002: local staging path isolation,
// directory creation, and recursive cleanup on source disposal.
public class StagingStorageServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly IConfiguration _config;
    private readonly StagingStorageService _service;

    public StagingStorageServiceTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "kh_staging_test_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Staging:Path"] = _tempRoot
            })
            .Build();
        _service = new StagingStorageService(_config, NullLogger<StagingStorageService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception ex) { _ = ex.Message; }
        }
    }

    [Fact]
    public void GetStagingDirectory_CreatesAndReturnsDirectory()
    {
        var sourceId = Guid.NewGuid();
        var dir = _service.GetStagingDirectory(sourceId);

        Assert.True(Directory.Exists(dir));
        Assert.Contains(sourceId.ToString("N"), dir);
    }

    [Fact]
    public async Task CleanupStagingAsync_RemovesSourceDirectoryAndContents()
    {
        var sourceId = Guid.NewGuid();
        var dir = _service.GetStagingDirectory(sourceId);
        var subFile = Path.Join(dir, "test.txt");
        await File.WriteAllTextAsync(subFile, "hello staging");

        Assert.True(File.Exists(subFile));

        await _service.CleanupStagingAsync(sourceId);

        Assert.False(Directory.Exists(dir));
    }
}
