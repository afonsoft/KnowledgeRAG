using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260924-cloud-storage-connectors via HTTP: source creation with
// credentials → hasKey mask, missing secret → 400, sync without reachable
// backend → failed status recorded (no crash).
public class CloudSourcesApiTests : IClassFixture<CloudSourcesApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-cloud-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private readonly HttpClient _client;

    public CloudSourcesApiTests(Fixture factory) => _client = TestAuth.Login(factory);

    [Fact]
    public async Task AwsS3_Create_StoresHasKey_NeverSecret()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"s3-{Guid.NewGuid():N}",
            type = "AwsS3",
            configuration = new
            {
                bucketName = "docs",
                region = "us-east-1",
                accessKeyId = "AKIDFAKE",
                secretAccessKey = "shhh-secret",
                prefix = "politicas/"
            },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;

        var config = source.Configuration!;
        Assert.True(config["hasKey"]!.GetValue<bool>());
        Assert.Equal("AKIDFAKE", config["accessKeyId"]!.GetValue<string>());
        Assert.Null(config["secretAccessKey"]);
    }

    [Fact]
    public async Task AwsS3_Create_WithoutSecret_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"s3-{Guid.NewGuid():N}",
            type = "AwsS3",
            configuration = new { bucketName = "docs", region = "us-east-1", accessKeyId = "AK" },
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AzureFiles_Create_RequiresShareName()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"az-{Guid.NewGuid():N}",
            type = "AzureFiles",
            configuration = new { connectionString = "UseDevelopmentStorage=true" },
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GoogleDrive_Create_InvalidSharedUrl_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"gd-{Guid.NewGuid():N}",
            type = "GoogleDrive",
            configuration = new { sharedUrl = "https://example.com/not-a-drive-link" },
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GoogleDrive_Create_StoresHasKeyFalse_WhenNoApiKey()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"gd-{Guid.NewGuid():N}",
            type = "GoogleDrive",
            configuration = new { sharedUrl = "https://drive.google.com/drive/folders/ABC123_xyz" },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        Assert.False(source.Configuration!["hasKey"]!.GetValue<bool>());
        Assert.Null(source.Configuration["apiKey"]);
    }

    [Fact]
    public async Task GoogleDrive_Create_WithApiKey_StoresSecretNotConfig()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"gd-{Guid.NewGuid():N}",
            type = "GoogleDrive",
            configuration = new
            {
                sharedUrl = "https://drive.google.com/drive/folders/ABC123_xyz",
                apiKey = "AIza-fake-key"
            },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        Assert.True(source.Configuration!["hasKey"]!.GetValue<bool>());
        Assert.Null(source.Configuration["apiKey"]);
    }

    [Fact]
    public async Task OciStorage_Sync_Unreachable_FailsGracefully()
    {
        var create = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"oci-{Guid.NewGuid():N}",
            type = "OciStorage",
            configuration = new
            {
                @namespace = "fake-ns",
                region = "sa-saopaulo-1",
                bucketName = "docs",
                accessKeyId = "AK",
                secretAccessKey = "secret"
            },
            isActive = true
        });
        create.EnsureSuccessStatusCode();
        var source = (await create.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;

        // Sync via compat mode — the unreachable endpoint must fail the sync,
        // not crash the pipeline.
        var sync = await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        sync.EnsureSuccessStatusCode();
        var result = (await sync.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal("failed", result.Status);

        var fresh = await _client.GetFromJsonAsync<KnowledgeSourceDto>($"/api/sources/{source.Id}");
        Assert.Equal("failed", fresh!.LastSyncStatus);
        Assert.NotEmpty(fresh.LastError ?? "");
    }
}
