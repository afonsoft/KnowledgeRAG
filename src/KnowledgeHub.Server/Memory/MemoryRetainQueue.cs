using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002 (mode=async): persisted operation
/// + in-process channel, mirroring <see cref="Ingestion.IIngestionQueue"/>.
/// Cancellation is cooperative: the worker links the op token so a cancelled
/// retain aborts mid-flight; the per-retain transaction guarantees no
/// partial units survive.
/// </summary>
public interface IMemoryRetainQueue
{
    ChannelReader<Guid> Reader { get; }
    Task<MemoryOperation> EnqueueAsync(MemoryBank bank, Guid apiKeyId, MemoryRetainRequest req, CancellationToken ct);
    bool TryCancel(Guid operationId);
    CancellationToken TokenFor(Guid operationId, CancellationToken stopping);
    void Complete(Guid operationId);
}

public sealed class MemoryRetainQueue(IServiceScopeFactory scopeFactory) : IMemoryRetainQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(capacity: 100) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancelTokens = new();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public async Task<MemoryOperation> EnqueueAsync(
        MemoryBank bank, Guid apiKeyId, MemoryRetainRequest req, CancellationToken ct)
    {
        var op = new MemoryOperation
        {
            BankId = bank.Id,
            ApiKeyId = apiKeyId,
            Kind = "retain",
            Status = "queued",
            PayloadJson = JsonSerializer.Serialize(req)
        };
        // The op row persists on its own scope — enqueue must survive the
        // caller's request scope (same pattern as IngestionQueue).
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        db.MemoryOperations.Add(op);
        await db.SaveChangesAsync(CancellationToken.None);
        _cancelTokens[op.Id] = new CancellationTokenSource();
        await _channel.Writer.WriteAsync(op.Id, ct);
        return op;
    }

    public bool TryCancel(Guid operationId)
    {
        if (!_cancelTokens.TryGetValue(operationId, out var cts))
            return false;
        cts.Cancel();
        return true;
    }

    public CancellationToken TokenFor(Guid operationId, CancellationToken stopping) =>
        _cancelTokens.TryGetValue(operationId, out var cts)
            ? CancellationTokenSource.CreateLinkedTokenSource(stopping, cts.Token).Token
            : stopping;

    public void Complete(Guid operationId)
    {
        if (_cancelTokens.TryRemove(operationId, out var cts))
            cts.Dispose();
    }
}
