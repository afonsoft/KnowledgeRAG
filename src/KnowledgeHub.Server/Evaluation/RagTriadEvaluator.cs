using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Evaluation;

/// <summary>
/// Deterministic RAG Quality Triad evaluator (SPEC-20260927-rag-evaluation-triad-metrics RF-002).
/// Cheap, offline heuristics — no LLM call — so it can run on every sampled answer
/// without inflating latency or cost (SPEC guardrail: minimize inference spend).
/// </summary>
public sealed class RagTriadEvaluator : IRagTriadEvaluator
{
    private static readonly Regex TokenRe = new(@"[a-z0-9]+(?:[./-][a-z0-9]+)*", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex SentenceRe = new(@"[^.!?;]+[.!?;]?", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex CitationRe = new(@"\[\d{1,3}\]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private const int MinTokenLen = 3;
    private const double MinClauseOverlap = 0.5;

    public RagEvaluationResult Evaluate(
        string question, IReadOnlyList<string> contextChunks, string answer,
        double hallucinationFloor = 0.60)
    {
        var ctxText = string.Join(" ", contextChunks ?? []);
        var ctxTokens = TokenSet(ctxText);

        // Strip [n] citation markers before scoring — the synthesizer requires
        // them (AnswerService.SystemPrompt), but their digits would otherwise be
        // graded as invented numbers and faithful answers would look hallucinated.
        var scoredAnswer = CitationRe.Replace(answer ?? "", " ");

        var contextRelevance = ContextRelevanceScore(question, contextChunks);
        var groundedness = GroundednessScore(scoredAnswer, ctxTokens);
        var answerRelevance = AnswerRelevanceScore(question, scoredAnswer);
        var overall = HarmonicMean(contextRelevance, groundedness, answerRelevance);

        return new RagEvaluationResult
        {
            ContextRelevance = contextRelevance,
            Groundedness = groundedness,
            AnswerRelevance = answerRelevance,
            OverallScore = overall,
            FlaggedAsHallucination = groundedness < hallucinationFloor
        };
    }

    // Context Relevance: share of context sentences that overlap the query lexically.
    private static double ContextRelevanceScore(string question, IReadOnlyList<string>? chunks)
    {
        var qTokens = TokenSet(question);
        if (qTokens.Count == 0) return 1.0;

        var sentences = Sentences(string.Join(" ", chunks ?? []));
        if (sentences.Count == 0) return 0.0;

        var relevant = sentences.Count(s => TokenSet(s).Overlaps(qTokens));
        return Clamp((double)relevant / sentences.Count);
    }

    // Groundedness: share of answer clauses supported by the context. A clause is
    // supported only when every numeric/date token appears in the context AND at
    // least MinClauseOverlap of its remaining content tokens do — a single shared
    // word must not mark an entire invented claim as grounded.
    private static double GroundednessScore(string answer, HashSet<string> ctxTokens)
    {
        var clauses = Sentences(answer);
        if (clauses.Count == 0) return 1.0;

        var supported = 0;
        foreach (var tokens in clauses.Select(TokenSet))
        {
            if (tokens.Count == 0) { supported++; continue; }

            var numbers = tokens.Where(t => t.Any(char.IsDigit)).ToList();
            // Invented numbers/dates → unsupported unless present in context.
            if (numbers.Any(n => !ctxTokens.Contains(n))) continue;

            var words = tokens.Count - numbers.Count;
            var wordOverlap = tokens.Count(t => ctxTokens.Contains(t) && !t.Any(char.IsDigit));
            if (words == 0 || wordOverlap >= Math.Ceiling(words * MinClauseOverlap))
                supported++;
        }
        return Clamp((double)supported / clauses.Count);
    }

    // Answer Relevance: lexical Jaccard between question and answer content tokens.
    private static double AnswerRelevanceScore(string question, string answer)
    {
        var q = TokenSet(question);
        var a = TokenSet(answer);
        if (q.Count == 0 || a.Count == 0) return 0.0;

        var inter = q.Count(t => a.Contains(t));
        var union = q.Union(a).Count();
        return Clamp((double)inter / union);
    }

    private static double HarmonicMean(double c, double g, double a)
    {
        if (c <= 0 || g <= 0 || a <= 0) return 0.0;
        return Clamp(3.0 * (c * g * a) / ((c * g) + (g * a) + (c * a)));
    }

    private static HashSet<string> TokenSet(string text)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(text)) return set;
        set.UnionWith(TokenRe.Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Any(char.IsDigit) || t.Length >= MinTokenLen));
        return set;
    }

    private static List<string> Sentences(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        list.AddRange(SentenceRe.Matches(text)
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 0));
        return list;
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
