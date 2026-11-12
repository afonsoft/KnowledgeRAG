using System.Collections.Concurrent;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Server.Memory.MentalModels;

public sealed record MentalModelRefreshResult(
    Guid ModelId,
    bool Refreshed,
    bool SkippedInFlight,
    bool SkippedMinInterval,
    int SourceUnits,
    string? Error = null);

/// <summary>One refresh in flight per model — second request is skipped (RF-002).</summary>
public sealed class MentalModelRefreshLocks
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    public bool TryEnter(Guid id) => _inFlight.TryAdd(id, 0);
    public void Exit(Guid id) => _inFlight.TryRemove(id, out _);
}

/// <summary>
/// SPEC-20261111-mental-models-pages RF-002: refresh = a bounded, read-only
/// synthesis pass over in-scope units (reflect-style: observations/facts in
/// scope → answer-as-document). mode 'full' re-synthesizes the whole scope;
/// 'delta' folds only units newer than the watermark into current content.
/// </summary>
public sealed class MentalModelRefreshService(
    KnowledgeHubDbContext db,
    MentalModelService models,
    MentalModelRefreshLocks locks,
    MemoryConfigResolver config,
    VaultPageProjector projector,
    IServiceProvider sp)
{
    private const int SourceLimit = 60;

    public async Task<MentalModelRefreshResult> RefreshAsync(
        Guid modelId, bool ignoreMinInterval, CancellationToken ct)
    {
        var model = await db.MemoryMentalModels
            .Include(m => m.Bank)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new MemoryNotFoundException("mental model not found");
        var trigger = MentalModelService.Trigger(model);

        if (!locks.TryEnter(model.Id))
            return new MentalModelRefreshResult(model.Id, false, true, false, 0);
        try
        {
            if (!ignoreMinInterval && model.LastRefreshedAt is { } last
                && DateTime.UtcNow - last < TimeSpan.FromSeconds(trigger.MinRefreshIntervalSeconds))
                return new MentalModelRefreshResult(model.Id, false, false, true, 0);

            var chat = sp.GetService<IChatClient>()
                ?? throw new MemoryServiceUnavailableException(
                    "mental model refresh requires a configured chat provider (Chat:*)");
            var runStart = DateTime.UtcNow;
            var units = await ScopedUnitsAsync(model, trigger, ct);
            var delta = trigger.Mode == "delta" && model.LastMemorySeenAt is { } wm
                ? units.Where(u => u.MentionedAt > wm).ToList()
                : units;
            if (delta.Count == 0 && model.Content.Length > 0)
            {
                // Nothing new — advance watermark, keep content.
                model.LastMemorySeenAt = runStart;
                model.LastRefreshError = null;
                await db.SaveChangesAsync(ct);
                return new MentalModelRefreshResult(model.Id, true, false, false, 0);
            }

            var prompt = BuildPrompt(model, trigger, delta);
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var content = response.Text?.Trim();
            if (content is null or { Length: 0 })
                throw new MemoryServiceUnavailableException("refresh produced empty content");
            // Strip a trailing citations block if the model emits one.
            content = StripCitationsBlock(content);

            model.Content = content;
            model.LastRefreshedAt = runStart;
            model.LastMemorySeenAt = runStart;
            model.LastRefreshError = null;
            await models.AppendHistoryAsync(model, manualEdit: false, ct);
            await db.SaveChangesAsync(ct);
            await TrimHistoryAsync(model.Id, config.MentalModelHistoryLimit(model.Bank), ct);
            await MentalModelService.InvalidatePageCacheAsync(sp, model, ct);
            await projector.ProjectAsync(model, model.Bank.BankKey, ct);
            return new MentalModelRefreshResult(model.Id, true, false, false, delta.Count);
        }
        catch (MemoryServiceUnavailableException ex)
        {
            model.LastRefreshError = ex.Message;
            await db.SaveChangesAsync(ct);
            return new MentalModelRefreshResult(model.Id, false, false, false, 0, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            model.LastRefreshError = ex.Message;
            await db.SaveChangesAsync(ct);
            return new MentalModelRefreshResult(model.Id, false, false, false, 0, ex.Message);
        }
        finally
        {
            locks.Exit(model.Id);
        }
    }

    /// <summary>In-scope units at the snapshot (created before run start).</summary>
    internal async Task<List<MemoryUnit>> ScopedUnitsAsync(
        MemoryMentalModel model, MentalModelTrigger trigger, CancellationToken ct)
    {
        var q = db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == model.BankId && !u.IsDeleted);
        if (trigger.FactTypes is not null)
            q = q.Where(u => trigger.FactTypes.Contains(u.FactType));
        var cands = await q.OrderByDescending(u => u.MentionedAt)
            .Take(trigger.Tags is { Count: > 0 } ? 500 : SourceLimit)
            .ToListAsync(ct);
        if (trigger.Tags is { Count: > 0 })
            cands = cands.Where(u => trigger.ScopeMatches(u.FactType, u.Tags))
                .Take(SourceLimit).ToList();
        return cands;
    }

    private static string BuildPrompt(
        MemoryMentalModel model, MentalModelTrigger trigger, List<MemoryUnit> units)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You maintain a living document that answers: \"{model.SourceQuery}\"");
        sb.AppendLine();
        if (trigger.Mode == "delta" && model.Content.Length > 0)
        {
            sb.AppendLine("CURRENT DOCUMENT:");
            sb.AppendLine(model.Content);
            sb.AppendLine();
            sb.AppendLine("NEW MEMORIES (since the last refresh — fold them in; keep everything still true):");
        }
        else
        {
            sb.AppendLine("MEMORIES (write the document grounded ONLY in these):");
        }
        foreach (var u in units)
            sb.AppendLine($"- [{u.FactType} {u.Id:N}] {u.Text}");
        if (units.Count == 0)
            sb.AppendLine("- (none)");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Output ONLY the document in markdown — no preamble, no commentary.");
        sb.AppendLine("- Ground every claim in the listed memories; if they are insufficient, write 'Sem evidência suficiente.'");
        sb.AppendLine("- Keep it concise and factual; use headings/bullets.");
        return sb.ToString();
    }

    private static string StripCitationsBlock(string content)
    {
        var idx = content.LastIndexOf("\n---\n", StringComparison.Ordinal);
        if (idx > 0 && content[(idx + 5)..].Contains("based_on", StringComparison.Ordinal))
            return content[..idx].TrimEnd();
        var j = content.LastIndexOf('{');
        if (j > 0 && content[j..].Contains("based_on", StringComparison.Ordinal))
            return content[..j].TrimEnd();
        return content;
    }

    private async Task TrimHistoryAsync(Guid modelId, int limit, CancellationToken ct)
    {
        var excess = await db.MentalModelHistories
            .Where(h => h.ModelId == modelId)
            .OrderByDescending(h => h.Version)
            .Skip(limit)
            .ToListAsync(ct);
        if (excess.Count > 0)
        {
            db.MentalModelHistories.RemoveRange(excess);
            await db.SaveChangesAsync(ct);
        }
    }
}
