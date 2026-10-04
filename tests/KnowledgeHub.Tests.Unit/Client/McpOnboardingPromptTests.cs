using System.Globalization;
using KnowledgeHub.Client.Services;

namespace KnowledgeHub.Tests.Unit.Client;

// The onboarding prompt is the same copy shown on Home and Login — it must
// embed the hub URL (mcp + sse + a2a + agent card) and follow the UI culture.
public class McpOnboardingPromptTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentUICulture;

    public void Dispose() => CultureInfo.CurrentUICulture = _original;

    [Fact]
    public void Build_English_ContainsConnectionAndA2aSections()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");

        var prompt = McpOnboardingPrompt.Build("https://hub.example.com");

        Assert.Contains("Configure the Knowledge Hub MCP server", prompt);
        Assert.Contains("https://hub.example.com/mcp", prompt);
        Assert.Contains("https://hub.example.com/mcp/sse", prompt);
        Assert.Contains("https://hub.example.com/.well-known/agent-card.json", prompt);
        Assert.Contains("https://hub.example.com/a2a", prompt);
        Assert.Contains("Authorization: Bearer", prompt);
    }

    [Fact]
    public void Build_Portuguese_UsesPtTemplate()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("pt-BR");

        var prompt = McpOnboardingPrompt.Build("https://hub.example.com");

        Assert.Contains("Configure o servidor MCP do Knowledge Hub", prompt);
        Assert.Contains("https://hub.example.com/mcp", prompt);
        Assert.DoesNotContain("Configure the Knowledge Hub MCP server", prompt);
    }

    [Fact]
    public void Build_Spanish_UsesEsTemplate()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("es-ES");

        var prompt = McpOnboardingPrompt.Build("https://hub.example.com");

        Assert.Contains("Configure el servidor MCP de Knowledge Hub", prompt);
        Assert.Contains("https://hub.example.com/mcp", prompt);
        Assert.DoesNotContain("Configure the Knowledge Hub MCP server", prompt);
    }

    [Fact]
    public void Build_OtherCulture_FallsBackToEnglish()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

        var prompt = McpOnboardingPrompt.Build("https://hub.example.com");

        Assert.Contains("Configure the Knowledge Hub MCP server", prompt);
    }
}
