using KnowledgeHub.Sdk;
using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.Knowledge;

/// <summary>
/// RF-007 — hub bridge via <see cref="KnowledgeHubClient"/> (MCP):
/// search_knowledge feeds the prompt; write_knowledge persists the review.
/// Hub unavailability degrades to local mode without failing the gate.
/// </summary>
public sealed class HubKnowledgeBridge : IAsyncDisposable
{
    private readonly KnowledgeHubClient? _client;
    public bool Reachable => _client is not null;

    private HubKnowledgeBridge(KnowledgeHubClient? client) => _client = client;

    public static async Task<HubKnowledgeBridge> ConnectAsync(ReviewOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.HubUrl) || string.IsNullOrWhiteSpace(options.HubApiKey))
            return new HubKnowledgeBridge(null);
        try
        {
            var client = await KnowledgeHubClient.ConnectAsync(options.HubUrl, options.HubApiKey, ct);
            return new HubKnowledgeBridge(client);
        }
        catch (Exception)
        {
            return new HubKnowledgeBridge(null);
        }
    }

    /// <summary>Repo conventions/patterns to inject into the review prompt.</summary>
    public async Task<IReadOnlyList<string>> SearchConventionsAsync(string repo, CancellationToken ct)
    {
        if (_client is null) return [];
        try
        {
            var result = await _client.SearchAsync(
                $"code review conventions {repo} C# SOLID style recurring findings", topK: 5, cancellationToken: ct);
            return result.Results.Select(r => $"[{r.DocumentTitle}] {r.ChunkText}").ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Hub reasoning mode: delegate the analysis to agent_chat.</summary>
    public async Task<string?> AgentChatAsync(string prompt, CancellationToken ct)
    {
        if (_client is null) return null;
        var result = await _client.AgentChatAsync(prompt, threadId: null, cancellationToken: ct);
        return result.Answer;
    }

    /// <summary>Persist the review as knowledge: review/{repo}/pr-{N}.</summary>
    public async Task PersistAsync(PullRequestSignal signal, AnalysisResult analysis, string verdict, CancellationToken ct)
    {
        if (_client is null) return;
        var body = $"""
            # Knowledge Review — PR #{signal.Number} ({signal.Repo})

            Verdict: **{verdict}** · {signal.Files.Count} file(s) · {signal.CollectedAt:u}

            {analysis.Summary}

            ## Findings
            {string.Join("\n", analysis.Findings.Select(f => $"- {f.Kind}/{f.Severity} {f.File}:{f.Line} {f.Cwe} — {f.Rationale}"))}
            """;
        try
        {
            await _client.WriteKnowledgeAsync(
                $"review/{signal.Repo}/pr-{signal.Number}", body, ct);
        }
        catch (Exception)
        {
            // Non-fatal — the PR review itself already landed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
