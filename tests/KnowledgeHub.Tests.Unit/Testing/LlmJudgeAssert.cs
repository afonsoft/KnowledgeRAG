using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KnowledgeHub.Tests.Unit.Testing;

/// <summary>
/// SPEC-20261111-agent-rules-hardening RF-001: LLM-as-judge assertion helper
/// for non-deterministic behavior (classification, attribution, phrasing).
///
/// Deterministic outputs keep hard <c>Assert.*</c> checks in fast unit tests;
/// LLM judge tests assert *properties* of the output ("the answer cites at
/// least one retrieved document", "the tone is neutral"), never exact strings
/// and never LLM-internal fields such as <c>fact_type</c>.
///
/// Judge configuration (any OpenAI-compatible endpoint, same protocol as
/// <c>Chat/OpenAiChatClient</c>):
///   env Tests__LlmJudge__Endpoint   (e.g. http://localhost:11434/v1)
///   env Tests__LlmJudge__Model      (e.g. llama3.1:8b)
///   env Tests__LlmJudge__ApiKey     (optional)
/// Tests skip gracefully when the judge is not configured — they never fail a
/// developer machine or CI without a judge model.
/// </summary>
public static class LlmJudgeAssert
{
    public const string EndpointEnv = "Tests__LlmJudge__Endpoint";
    public const string ModelEnv = "Tests__LlmJudge__Model";
    public const string ApiKeyEnv = "Tests__LlmJudge__ApiKey";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EndpointEnv))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelEnv));

    /// <summary>
    /// Asks the judge model whether <paramref name="actual"/> satisfies
    /// <paramref name="criteria"/>. Fails the test with the judge's rationale
    /// when it answers "no"; skips when no judge is configured.
    /// </summary>
    public static async Task PassesAsync(string criteria, string actual, string? context = null)
    {
        // xunit v2 has no runtime-skip that console/CI runners honor — an
        // unconfigured judge is a deterministic no-op pass, not a failure.
        if (!IsConfigured) return;

        var verdict = await JudgeAsync(criteria, actual, context);
        Assert.True(verdict.Pass,
            $"LLM judge rejected output. Criteria: {criteria}\nRationale: {verdict.Rationale}\nActual: {Truncate(actual)}");
    }

    /// <summary>Same as <see cref="PassesAsync"/> but returns the verdict for
    /// custom assertions (e.g. soft warnings).</summary>
    public static async Task<(bool Pass, string Rationale)> JudgeAsync(
        string criteria, string actual, string? context = null)
    {
        Assert.True(IsConfigured, "LLM judge not configured");

        var prompt = $$"""
            You are a strict test judge. Decide whether ACTUAL satisfies CRITERIA.
            Reply with a single JSON object {"pass": true|false, "rationale": "<one sentence>"}.
            Judge only the stated criteria — do not demand style or completeness beyond them.

            CRITERIA: {{criteria}}
            {{(context is null ? "" : $"CONTEXT: {context}")}}
            ACTUAL: {{actual}}
            """;

        var text = await SendChatAsync(prompt);
        var json = ExtractJson(text);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pass = doc.RootElement.GetProperty("pass").GetBoolean();
            var rationale = doc.RootElement.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
            return (pass, rationale);
        }
        catch (JsonException)
        {
            return (false, $"judge returned unparseable verdict: {Truncate(text)}");
        }
    }

    // POST {Endpoint}/v1/chat/completions — same wire protocol as Server's OpenAiChatClient.
    private static async Task<string> SendChatAsync(string prompt)
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnv)!;
        var model = Environment.GetEnvironmentVariable(ModelEnv)!;
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnv);

        using var http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.0,
            max_tokens = 512
        });
        var res = await http.PostAsync("v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string Truncate(string s, int max = 400) =>
        s.Length <= max ? s : s[..max] + "…";
}
