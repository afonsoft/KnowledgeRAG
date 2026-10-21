using System.Text;
using KnowledgeHub.Server.Assistant;
using KnowledgeHub.Server.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Context;

/// <summary>
/// Per-item compaction core — SPEC-20261004-context-snapshots RF-006
/// (Tier 4: deterministic elision + optional LLM summarization).
/// <para>
/// This is a NEW item-level pipeline, not a reuse of
/// <c>ChainCompactor</c>: that compactor folds whole AST sections
/// (<c>ChatMessage</c> pairs) inside the agent window, while here each
/// <see cref="ContextItem"/> is elided/dropped individually against an
/// explicit <paramref name="tokenBudget"/>. The deterministic fallback
/// mirrors <c>ChainCompactor.BuildSummary</c>'s budgeted excerpt approach.
/// </para>
/// Never throws for LLM problems — summarization failure degrades to the
/// deterministic fallback (<c>summaryFallback=true</c>).
/// </summary>
public sealed class ContextCompactionService
{
    public const string StrategyNone = "none";
    public const string StrategyElision = "elision";
    public const string StrategyElisionSummary = "elision+summary";

    private readonly IAssistantChatClientProvider _assistants;
    private readonly ContextOptions _options;
    private readonly ILogger<ContextCompactionService> _logger;

    public ContextCompactionService(
        IAssistantChatClientProvider assistants,
        IOptions<ContextOptions> options,
        ILogger<ContextCompactionService> logger)
    {
        _assistants = assistants;
        _options = options.Value;
        _logger = logger;
    }

    public sealed record CompactionStats(
        int TokensBefore,
        int TokensAfter,
        int Budget,
        int DroppedCount,
        int ElidedCount,
        bool SummaryUsed,
        bool SummaryFallback,
        bool CacheHit = false);

    public sealed record CompactionResult(
        List<ContextItem> Items,
        CompactionStats Stats,
        bool WithinBudget);

    /// <summary>Estimativa canônica ~4 chars/token (inclui metadados do item).</summary>
    private static int TokensOf(ContextItem item) => ContextPayload.EstimateTokens(item);

    private static int TotalTokens(IEnumerable<ContextItem> items) => items.Sum(TokensOf);

    /// <summary>
    /// Aplica a estratégia sobre uma CÓPIA dos itens — o snapshot persistido
    /// nunca é mutado (RF-005).
    /// </summary>
    public async Task<CompactionResult> CompactAsync(
        IReadOnlyList<ContextItem> source,
        string strategy,
        int tokenBudget,
        CancellationToken ct)
    {
        var items = source.Select(Clone).ToList();
        var tokensBefore = TotalTokens(items);
        var elided = 0;
        var dropped = 0;
        var summaryUsed = false;
        var summaryFallback = false;

        if (strategy is StrategyElision or StrategyElisionSummary)
            elided = Elide(items, tokenBudget);

        if (strategy == StrategyElisionSummary && TotalTokens(items) > tokenBudget)
        {
            var summarized = await TrySummarizeAsync(items, tokenBudget, ct);
            if (summarized is not null)
            {
                items = summarized.Items;
                summaryUsed = summarized.UsedLlm;
                summaryFallback = !summarized.UsedLlm;
            }
        }

        // Estágio final (todas as estratégias): corte por budget —
        // passage de menor score, depois tool_output mais antigo;
        // decision/note/summary nunca são removidos (RF-006 invariante).
        dropped += DropToFit(items, tokenBudget);

        var after = TotalTokens(items);
        return new CompactionResult(
            items,
            new CompactionStats(tokensBefore, after, tokenBudget,
                dropped, elided, summaryUsed, summaryFallback),
            after <= tokenBudget);
    }

    private static ContextItem Clone(ContextItem i) => new()
    {
        Kind = i.Kind,
        Id = i.Id,
        Title = i.Title,
        Path = i.Path,
        Tool = i.Tool,
        Content = i.Content,
        Score = i.Score
    };

    /// <summary>
    /// Estágio 1 — elision determinística: <c>tool_output</c>/<c>passage</c>
    /// fora do keep-tail de <see cref="ContextOptions.KeepMinLastItems"/> e
    /// acima de <see cref="ContextOptions.ElisionTokenLimit"/> viram stub.
    /// Idempotente: stubs já elididos não re-elidem.
    /// </summary>
    private int Elide(List<ContextItem> items, int tokenBudget)
    {
        var elided = 0;
        var keepFrom = Math.Max(0, items.Count - _options.KeepMinLastItems);
        for (var i = 0; i < keepFrom; i++)
        {
            var item = items[i];
            if (!ContextKinds.IsDisposable(item.Kind))
                continue;
            var tokens = TokensOf(item);
            if (tokens <= _options.ElisionTokenLimit)
                continue;
            if (item.Content.StartsWith("[Output elided:", StringComparison.Ordinal))
                continue;
            var lines = item.Content.Count(ch => ch == '\n') + 1;
            item.Content = $"[Output elided: {tokens} tokens, {lines} lines. " +
                           $"Tool: {item.Tool ?? "unknown"}. Ref: none]";
            item.Score = null;
            elided++;
        }
        return elided;
    }

    private sealed record SummaryOutcome(List<ContextItem> Items, bool UsedLlm);

    /// <summary>
    /// Estágio 2 — summarization via rota <c>summarize</c> do
    /// <see cref="IAssistantChatClientProvider"/> com timeout próprio e
    /// fallback determinístico. Nunca lança.
    /// </summary>
    private async Task<SummaryOutcome?> TrySummarizeAsync(
        List<ContextItem> items, int tokenBudget, CancellationToken ct)
    {
        // Elegíveis: os descartáveis mais antigos até SummaryInputMaxChars.
        var budget = _options.SummaryInputMaxChars;
        var eligible = new List<int>();
        var chars = 0;
        for (var i = 0; i < items.Count && chars < budget; i++)
        {
            if (!ContextKinds.IsDisposable(items[i].Kind))
                continue;
            eligible.Add(i);
            chars += items[i].Content.Length;
        }
        if (eligible.Count == 0)
            return null;

        string? summaryText = null;
        var usedLlm = false;
        var client = _assistants.ForSubtask("summarize", null);
        if (client is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.SummaryTimeoutSeconds));
                var sb = new StringBuilder();
                foreach (var i in eligible)
                {
                    var it = items[i];
                    sb.AppendLine($"--- {it.Kind}" +
                        (it.Title is not null ? $" | {it.Title}" : "") +
                        (it.Tool is not null ? $" | tool:{it.Tool}" : "") + " ---");
                    sb.AppendLine(it.Content.Length > budget
                        ? it.Content[..budget]
                        : it.Content);
                }
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User,
                        "Summarize the following work-context excerpts into a compact handoff note " +
                        $"of at most {_options.SummaryMaxTokens} tokens. Preserve decisions, identifiers " +
                        "and file paths verbatim.\n\n" + sb)],
                    new ChatOptions { MaxOutputTokens = _options.SummaryMaxTokens * 2 },
                    timeout.Token);
                summaryText = response.Text;
                usedLlm = !string.IsNullOrWhiteSpace(summaryText);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Context summarization failed — deterministic fallback");
                KnowledgeHubMetrics.AssistantFallbacks.Add(1);
            }
        }

        summaryText ??= BuildDeterministicSummary(items, eligible);
        var summaryItem = new ContextItem
        {
            Kind = ContextKinds.Summary,
            Title = "Compacted context summary",
            Content = summaryText
        };

        // Substitui os itens elegíveis pelo summary no lugar do primeiro,
        // preservando a ordem relativa dos restantes (invariante RF-006).
        var result = new List<ContextItem>(items.Count - eligible.Count + 1);
        var eligibleSet = eligible.ToHashSet();
        var inserted = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (!eligibleSet.Contains(i))
            {
                result.Add(items[i]);
            }
            else if (!inserted)
            {
                result.Add(summaryItem);
                inserted = true;
            }
        }
        return new SummaryOutcome(result, usedLlm);
    }

    /// <summary>
    /// Fallback determinístico (espelha a abordagem de
    /// <c>ChainCompactor.BuildSummary</c>): marcador + títulos + recorte
    /// orçamentado dos conteúdos elegíveis.
    /// </summary>
    private string BuildDeterministicSummary(List<ContextItem> items, List<int> eligible)
    {
        var sb = new StringBuilder("**summarized content:**\n");
        var budget = _options.SummaryInputMaxChars;
        foreach (var i in eligible)
        {
            var it = items[i];
            var head = $"[{it.Kind}]" +
                (it.Title is not null ? $" {it.Title}:" : "") +
                (it.Tool is not null ? $" (tool:{it.Tool})" : "");
            sb.AppendLine(head);
            var take = Math.Min(Math.Max(budget / eligible.Count, 256), it.Content.Length);
            sb.AppendLine(take < it.Content.Length
                ? it.Content[..take] + "…"
                : it.Content);
            if (sb.Length > _options.SummaryInputMaxChars)
                break;
        }
        if (sb.Length > _options.SummaryInputMaxChars)
            sb.Length = _options.SummaryInputMaxChars;
        return sb.ToString();
    }

    /// <summary>
    /// Remove até caber no budget: <c>passage</c> de menor score primeiro,
    /// depois <c>tool_output</c> mais antigo. Nunca remove
    /// decision/note/summary. Retorna o nº de removidos.
    /// </summary>
    private static int DropToFit(List<ContextItem> items, int tokenBudget)
    {
        var dropped = 0;
        while (TotalTokens(items) > tokenBudget)
        {
            var victim = -1;
            var minScore = double.MaxValue;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Kind != ContextKinds.Passage)
                    continue;
                var s = items[i].Score ?? 0;
                if (s < minScore) { minScore = s; victim = i; }
            }
            if (victim < 0)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i].Kind == ContextKinds.ToolOutput) { victim = i; break; }
                }
            }
            if (victim < 0)
                break; // só restam protegidos — withinBudget=false
            items.RemoveAt(victim);
            dropped++;
        }
        return dropped;
    }
}
