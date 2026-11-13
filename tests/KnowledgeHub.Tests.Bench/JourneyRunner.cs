using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Bench;

/// <summary>
/// SPEC-20261113-agent-journey-bench RF-001: executes a named journey —
/// an ordered list of MCP tools/call steps — against the in-process host
/// (public seams only, same pattern as KnowledgeHub.Tests.Blackbox) while
/// recording per-call wall time and payload sizes.
/// Deterministic mode: zero LLM config → LLM-dependent paths degrade the
/// same way they do for a real client without an LLM endpoint.
/// </summary>
public sealed class JourneyRunner
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-bench-{Guid.NewGuid():N}.db")
                }));
        }
    }

    private readonly WebApplicationFactory<Program> _factory;
    private HttpClient? _http;

    public JourneyRunner(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Authenticates once and opens the MCP session; reused across
    /// journeys (the factory DB is shared, so a second password change
    /// would 401).</summary>
    public async Task ConnectAsync(CancellationToken ct = default) =>
        _http ??= await AuthedMcpClientAsync(ct);

    /// <summary>Runs all steps in order; throws JourneyAssertionException on
    /// the first failing assertion (naming the journey + step index).</summary>
    public async Task<JourneyResult> RunAsync(Journey journey, CancellationToken ct = default)
    {
        var result = new JourneyResult { Name = journey.Name };
        var http = _http ??= await AuthedMcpClientAsync(ct);

        for (var i = 0; i < journey.Steps.Count; i++)
        {
            var step = journey.Steps[i];
            StepOutcome outcome;
            var attempts = 0;
            while (true)
            {
                attempts++;
                outcome = await CallToolAsync(http, step.Tool, step.Args(result.Outcomes), result, ct);
                var failure = step.Assert?.Invoke(outcome);
                if (failure is null)
                    break;
                if (attempts >= step.PollMax)
                    throw new JourneyAssertionException(
                        $"journey '{journey.Name}' step {i} ({step.Tool}) failed after {attempts} call(s): {failure}");
                await Task.Delay(step.PollDelayMs, ct);
            }
            result.Outcomes.Add(outcome);
        }
        return result;
    }

    private async Task<StepOutcome> CallToolAsync(
        HttpClient http, string tool, object args, JourneyResult result, CancellationToken ct)
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" +
            tool + "\",\"arguments\":" + JsonSerializer.Serialize(args) + "}}";
        var sw = Stopwatch.StartNew();
        var res = await http.PostAsync("/mcp",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var payload = await res.Content.ReadAsStringAsync(ct);
        sw.Stop();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{tool}: HTTP {(int)res.StatusCode}: {payload} || req={body}", null, res.StatusCode);

        var (callResult, isError) = ExtractCallResult(payload);
        var structured = callResult.TryGetProperty("structuredContent", out var sc)
            ? sc.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        result.Steps.Add(new StepMetric(
            tool, sw.ElapsedMilliseconds,
            Encoding.UTF8.GetByteCount(body),
            Encoding.UTF8.GetByteCount(payload),
            isError));
        return new StepOutcome { Tool = tool, Structured = structured, IsError = isError };
    }

    // Same public flow as the blackbox smoke: admin login → clear the
    // first-access gate → mint an aft_* key → MCP session over Bearer.
    // On a reused host the password may already be rotated — try both.
    private async Task<HttpClient> AuthedMcpClientAsync(CancellationToken ct)
    {
        var http = _factory.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "123qwe" }, ct);
        if (!login.IsSuccessStatusCode)
        {
            login = await http.PostAsJsonAsync("/api/auth/login",
                new { username = "admin", password = "bench-123" }, ct);
        }
        else
        {
            (await http.PostAsJsonAsync("/api/auth/change-password",
                new { currentPassword = "123qwe", newPassword = "bench-123" }, ct))
                .EnsureSuccessStatusCode();
        }
        login.EnsureSuccessStatusCode();

        var keyRes = await http.PostAsJsonAsync("/api/apikeys", new { name = "bench" }, ct);
        keyRes.EnsureSuccessStatusCode();
        using var keyDoc = JsonDocument.Parse(await keyRes.Content.ReadAsStringAsync(ct));
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", keyDoc.RootElement.GetProperty("key").GetString()!);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var init = await http.PostAsync("/mcp", new StringContent("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"bench","version":"1.0"}}}
            """, Encoding.UTF8, "application/json"), ct);
        init.EnsureSuccessStatusCode();
        if (init.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            http.DefaultRequestHeaders.Add("Mcp-Session-Id", sessionIds.First());
        http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        (await http.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Encoding.UTF8, "application/json"), ct)).EnsureSuccessStatusCode();
        return http;
    }

    private static (JsonElement Result, bool IsError) ExtractCallResult(string payload)
    {
        // Streamable HTTP may answer with an SSE body — take the last data: line.
        var data = payload.Contains("data:", StringComparison.Ordinal)
            ? payload.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l[5..].Trim()).Last()
            : payload;
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"JSON-RPC error: {err.GetRawText()}");
        var result = doc.RootElement.GetProperty("result");
        return (result.Clone(), result.TryGetProperty("isError", out var e) && e.GetBoolean());
    }
}

public sealed class JourneyAssertionException(string message) : Exception(message);
