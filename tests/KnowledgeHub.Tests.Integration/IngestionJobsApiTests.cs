using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260926-ingestion-jobs-test-deflake: this class depends on a shared
/// single-worker ingestion queue, so it is timing-sensitive to CPU contention
/// from sibling test classes (each parallel WebApplicationFactory hosts its own
/// worker). DisableParallelization keeps it serialized against the whole suite —
/// a single tiny-file ingest can exceed 60s when dozens of hosts fight for CPU.
/// </summary>
[CollectionDefinition(nameof(IngestionSerialCollection), DisableParallelization = true)]
public sealed class IngestionSerialCollection;

// Covers SPEC-20260925-new-endpoints-integration-tests: ingestion job endpoints,
// reindex, cancel, and the async sync contract end-to-end over HTTP.
[Collection(nameof(IngestionSerialCollection))]
public class IngestionJobsApiTests : IClassFixture<IngestionJobsApiTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-jobs-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private readonly Fixture _factory;
    private readonly HttpClient _client;
    private readonly string _dir;

    public IngestionJobsApiTests(Fixture factory)
    {
        _factory = factory;
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"jobs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) { _ = ex.Message; } }

    private async Task<KnowledgeSourceDto> SeedSource()
    {
        await File.WriteAllTextAsync(Path.Join(_dir, $"doc-{Guid.NewGuid():N}.txt"), "job test content");
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"jobs-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
    }

    private async Task<JsonElement> WaitTerminalJobAsync(Guid jobId)
    {
        // SPEC-20260926-ingestion-jobs-test-deflake: the CI flakes (runs
        // 36192973074, 36204860120) were polling-budget failures — a single
        // tiny-file ingest can exceed 60s when dozens of sibling hosts share
        // the CI runner's CPUs. Instead of a bigger budget, subscribe to the
        // IngestionProgressFeed terminal event (published AFTER the outcome is
        // persisted) — the 300s backstop covers CI thread-pool starvation
        // (diagnosed: jobs sit `queued` for >120s on loaded runners), not a
        // timing budget for the pipeline itself.
        var feed = _factory.Services.GetRequiredService<IIngestionProgressFeed>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(IngestionProgressEvent e)
        {
            if (e.JobId == jobId && e.Status is "done" or "failed" or "cancelled")
                terminal.TrySetResult();
        }
        feed.Published += OnPublished;
        try
        {
            // Subscribe before the state check so a job finishing in between
            // cannot be missed.
            var current = await GetJobAsync(jobId);
            if (current is not null)
                return current.Value;
            try
            {
                await terminal.Task.WaitAsync(TimeSpan.FromSeconds(300));
            }
            catch (TimeoutException)
            {
                // Report the row's last persisted state plus the whole queue —
                // a sibling job stuck "running" means the single-worker channel
                // is wedged behind it; nothing running at all means the worker
                // died or never dequeued (channel stall / host fault).
                var probe = await _client.GetFromJsonAsync<JsonElement>(
                    $"/api/ingestion/jobs/{jobId}");
                var all = await _client.GetFromJsonAsync<JsonElement>(
                    "/api/ingestion/jobs");
                var running = new List<string>();
                var queued = 0;
                foreach (var j in all.EnumerateArray())
                {
                    var st = j.GetProperty("status").GetString();
                    if (st == "running")
                        running.Add($"{j.GetProperty("id")} ({j.GetProperty("kind").GetString()})");
                    else if (st == "queued")
                        queued++;
                }
                throw new TimeoutException(
                    $"job {jobId} did not reach terminal state in 300s — " +
                    $"last status: {probe.GetProperty("status").GetString()}, " +
                    $"running: [{string.Join(", ", running)}], queued count: {queued}");
            }
            return (await GetJobAsync(jobId))!.Value;
        }
        finally
        {
            feed.Published -= OnPublished;
        }

        async Task<JsonElement?> GetJobAsync(Guid id)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/ingestion/jobs/{id}");
            return job.GetProperty("status").GetString() is "done" or "failed" or "cancelled"
                ? job
                : null;
        }
    }

    [Fact]
    public async Task Sync_EnqueuesJob_AndJobReachesTerminal()
    {
        var source = await SeedSource();

        var enqueue = await _client.PostAsync($"/api/sources/{source.Id}/sync", null);
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var body = await enqueue.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = body.GetProperty("jobId").GetGuid();
        Assert.False(jobId == Guid.Empty);
        Assert.Contains("existing", body.EnumerateObject().Select(p => p.Name));

        var job = await WaitTerminalJobAsync(jobId);
        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("docsProcessed").GetInt32() >= 1);
        Assert.True(job.GetProperty("chunksCreated").GetInt32() >= 1);
        Assert.Equal("sync", job.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Sync_WaitTrue_ReturnsSyncResult()
    {
        var source = await SeedSource();
        var response = await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<SyncResultDto>())!;
        Assert.Equal("completed", result.Status);
        Assert.True(result.DocumentsProcessed >= 1);
    }

    [Fact]
    public async Task Jobs_List_FiltersBySource()
    {
        var a = await SeedSource();
        var b = await SeedSource();
        var ea = await (await _client.PostAsync($"/api/sources/{a.Id}/sync", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var eb = await (await _client.PostAsync($"/api/sources/{b.Id}/sync", null))
            .Content.ReadFromJsonAsync<JsonElement>();

        var jobs = await _client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/ingestion/jobs?sourceId={a.Id}&limit=10");
        Assert.NotEmpty(jobs!);
        Assert.All(jobs!, j => Assert.Equal(a.Id, j.GetProperty("sourceId").GetGuid()));

        // Drain the shared single-worker queue so the next test does not wait
        // behind these jobs (SPEC-20260926-ingestion-jobs-test-deflake).
        await WaitTerminalJobAsync(ea.GetProperty("jobId").GetGuid());
        await WaitTerminalJobAsync(eb.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task Reindex_EnqueuesReindexJob()
    {
        var source = await SeedSource();
        await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        var enqueue = await _client.PostAsync($"/api/sources/{source.Id}/reindex", null);
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var jobId = (await enqueue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        var job = await WaitTerminalJobAsync(jobId);
        Assert.Equal("reindex", job.GetProperty("kind").GetString());
        Assert.Equal("done", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_NonExistent_Returns404()
    {
        var response = await _client.PostAsync($"/api/ingestion/jobs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_TerminalJob_Returns409()
    {
        var source = await SeedSource();
        var enqueue = await _client.PostAsync($"/api/sources/{source.Id}/sync", null);
        var jobId = (await enqueue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        await WaitTerminalJobAsync(jobId);

        var cancel = await _client.PostAsync($"/api/ingestion/jobs/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
    }
}
