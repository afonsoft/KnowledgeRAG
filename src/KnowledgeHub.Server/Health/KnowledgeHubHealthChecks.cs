using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KnowledgeHub.Server.Health;

/// <summary>SPEC-20260914-health-checks: DB reachable and migrated (ready).</summary>
public sealed class DatabaseHealthCheck(KnowledgeHubDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("database reachable")
                : HealthCheckResult.Unhealthy("database unreachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database check failed", ex);
        }
    }
}

/// <summary>Embedding provider resolvable, reporting a model/dimensions AND
/// reachable — a down endpoint degrades readiness instead of serving traffic
/// that cannot search/ingest (RF-607).</summary>
public sealed class EmbeddingHealthCheck(IEmbeddingProvider provider) : IHealthCheck
{
    // Probe result cached — health endpoints are scraped frequently; the
    // embed call itself is the reachability signal, not a per-request cost.
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    private DateTimeOffset _probeAt = DateTimeOffset.MinValue;
    private bool _probeOk = true;
    private string _probeDetail = "";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (provider.Dimensions <= 0 || string.IsNullOrWhiteSpace(provider.ModelId))
                return HealthCheckResult.Degraded("embedding provider misconfigured");

            if (DateTimeOffset.UtcNow - _probeAt > ProbeTtl)
            {
                try
                {
                    var probe = await provider.EmbedAsync("health probe", cancellationToken);
                    _probeOk = probe is { Length: > 0 };
                    _probeDetail = _probeOk ? "" : "empty embedding returned";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _probeOk = false;
                    _probeDetail = ex.GetBaseException().Message;
                }
                _probeAt = DateTimeOffset.UtcNow;
            }

            return _probeOk
                ? HealthCheckResult.Healthy($"provider={provider.ModelId} dims={provider.Dimensions}")
                : HealthCheckResult.Unhealthy($"embedding provider unreachable: {_probeDetail}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("embedding provider check failed", ex);
        }
    }
}

/// <summary>Ingestion service resolvable (watcher/sync pipeline wired).</summary>
public sealed class IngestionHealthCheck(IIngestionService ingestion) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(ingestion is not null
            ? HealthCheckResult.Healthy("ingestion initialized")
            : HealthCheckResult.Unhealthy("ingestion service missing"));
}
