using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261114-memory-session-lifecycle: session lifecycle events as
/// <see cref="MemoryOperation"/> rows kind="session" — zero migration.
/// begin → status "running" + payload {sessionId,label}; end → status "done"
/// + ResultJson {summary}. Open sessions = running rows; the context digest
/// surfaces them so an agent sees where the last session stopped.
/// </summary>
public sealed class MemorySessionService(
    KnowledgeHubDbContext db, MemoryBankService banks, IMemoryDefenseScreen defense)
{
    public const string SessionKind = "session";
    private const string StatusRunning = "running";
    private const string StatusDone = "done";
    private const int LabelCap = 500;
    private const int SummaryCap = 2000;

    public sealed record SessionDto(
        string SessionId, string? Label, string Status,
        DateTime StartedAt, DateTime? FinishedAt, string? Summary);

    public sealed record BeginResult(
        string SessionId, Guid BankId, DateTime StartedAt, bool AlreadyOpen);

    public sealed record EndResult(
        string SessionId, bool Ended, bool AlreadyClosed, long? DurationMs = null);

    public async Task<BeginResult> BeginAsync(
        string? bankKey, string? sessionId, string? label, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var scope = await banks.ScopeKeyAsync(ct);
        var id = string.IsNullOrWhiteSpace(sessionId)
            ? $"sess-{Guid.NewGuid():N}"[..12]
            : sessionId.Trim();
        if (id.Length > 128)
            throw new MemoryBadRequestException("sessionId exceeds 128 characters");
        // Session ids land in PayloadJson and are matched by containment —
        // restrict to a JSON-safe charset so the lookup is exact.
        if (id.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not ':'))
            throw new MemoryBadRequestException(
                "sessionId may only contain letters, digits, '.', '-', '_' and ':'");
        if (label?.Length > LabelCap)
            throw new MemoryBadRequestException($"label exceeds {LabelCap} characters");

        var open = await db.MemoryOperations
            .FirstOrDefaultAsync(o => o.BankId == bank.Id && o.Kind == SessionKind
                && o.Status == StatusRunning && o.PayloadJson.Contains($"\"sessionId\":\"{id}\""), ct);
        if (open is not null)
            return new BeginResult(id, bank.Id, open.StartedAt ?? open.CreatedAt, true);

        var op = new MemoryOperation
        {
            BankId = bank.Id,
            ApiKeyId = scope,
            Kind = SessionKind,
            Status = StatusRunning,
            PayloadJson = JsonSerializer.Serialize(new { sessionId = id, label }),
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.MemoryOperations.Add(op);
        await db.SaveChangesAsync(ct);
        return new BeginResult(id, bank.Id, op.StartedAt.Value, false);
    }

    public async Task<EndResult> EndAsync(
        string? bankKey, string sessionId, string? summary, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var id = sessionId.Trim();
        if (id.Length > 128 || id.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not ':'))
            throw new MemoryBadRequestException(
                "sessionId exceeds 128 characters or contains invalid characters");
        if (summary?.Length > SummaryCap)
            throw new MemoryBadRequestException($"summary exceeds {SummaryCap} characters");

        var op = await db.MemoryOperations
            .Where(o => o.BankId == bank.Id && o.Kind == SessionKind
                && o.PayloadJson.Contains($"\"sessionId\":\"{id}\""))
            .OrderByDescending(o => o.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (op is null || op.Status != StatusRunning)
            return new EndResult(id, false, true);

        // Defense-screened like retained content — MemoryDefenseException
        // maps to 422 via the standard handlers.
        var screened = await defense.ScreenAsync(summary ?? "", bank, ct);

        var now = DateTime.UtcNow;
        op.Status = StatusDone;
        op.FinishedAt = now;
        op.ResultJson = JsonSerializer.Serialize(new { summary = screened });
        await db.SaveChangesAsync(ct);
        return new EndResult(id, true, false,
            (long)(now - (op.StartedAt ?? op.CreatedAt)).TotalMilliseconds);
    }

    public async Task<IReadOnlyList<SessionDto>> ListAsync(
        string? bankKey, bool openOnly, int limit, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var rows = await db.MemoryOperations.AsNoTracking()
            .Where(o => o.BankId == bank.Id && o.Kind == SessionKind
                && (!openOnly || o.Status == StatusRunning))
            .OrderByDescending(o => o.StartedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    internal static SessionDto ToDto(MemoryOperation o)
    {
        string? sessionId = null, label = null, summary = null;
        try
        {
            using var doc = JsonDocument.Parse(o.PayloadJson);
            sessionId = doc.RootElement.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            label = doc.RootElement.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString() : null;
        }
        catch (JsonException)
        {
            // Malformed payload — fall back to the op row fields below.
        }
        if (o.ResultJson is { } rj)
        {
            try
            {
                using var doc = JsonDocument.Parse(rj);
                summary = doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
            }
            catch (JsonException)
            {
                // Malformed result — summary stays null.
            }
        }
        return new SessionDto(sessionId ?? o.Id.ToString("N"), label, o.Status,
            o.StartedAt ?? o.CreatedAt, o.FinishedAt, summary);
    }
}
