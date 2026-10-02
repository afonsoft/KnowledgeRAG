using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Chat;
using Microsoft.Extensions.AI;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Mesma regra do OpenAiChatClient: o request Ollama sempre sai com valores
// concretos — options {temperature, num_predict}, tools [] e, por mensagem,
// tool_calls [] / tool_call_id "". Tool sem schema recebe parameters default.
public sealed class OllamaChatClientTests : IDisposable
{
    private readonly List<IDisposable> _owned = new();
    /// <summary>Handler fake que captura o corpo da requisição e responde um chat mínimo.</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        /// <summary>Corpo JSON da última requisição capturada.</summary>
        public string? LastBody { get; private set; }

        /// <summary>Lê o corpo do request e devolve uma resposta Ollama mínima.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"message":{"role":"assistant","content":"ok"}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Tool fake sem schema de parameters (JsonSchema indefinido).</summary>
    private sealed class SchemaLessTool : AIFunction
    {
        public override string Name => "schema_less";
        public override string Description => "tool sem parameters";
        public override JsonElement JsonSchema => default;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new((object?)null);
    }

    /// <summary>Monta um OllamaChatClient apontando para o handler de captura.</summary>
    private (OllamaChatClient Client, CaptureHandler Handler) Sut()
    {
        var handler = new CaptureHandler();
        var http = new HttpClient(handler);
        _owned.Add(http);
        _owned.Add(handler);
        var client = new OllamaChatClient(http, new ChatProviderOptions
        {
            Provider = "ollama",
            Endpoint = "http://ollama.test",
            Model = "m"
        });
        return (client, handler);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var d in _owned)
            d.Dispose();
    }

    /// <summary>options sempre sai com temperature/num_predict, mesmo sem configuração.</summary>
    [Fact]
    public async Task Request_SendsSamplingDefaults()
    {
        var (client, handler) = Sut();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")]);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"temperature\":1", handler.LastBody!);
        Assert.Contains("\"num_predict\":4096", handler.LastBody!);
        Assert.Contains("\"tools\":[]", handler.LastBody!);
        Assert.Contains("\"stream\":false", handler.LastBody!);
        Assert.DoesNotContain("null", handler.LastBody!);
    }

    /// <summary>Mensagens sempre carregam tool_calls [] e tool_call_id "" — nunca null.</summary>
    [Fact]
    public async Task Request_MessageToolFields_Defaulted()
    {
        var (client, handler) = Sut();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")]);

        Assert.Contains("\"tool_calls\":[]", handler.LastBody!);
        Assert.Contains("\"tool_call_id\":\"\"", handler.LastBody!);
    }

    /// <summary>Tool sem schema recebe parameters {"type":"object","properties":{}}.</summary>
    [Fact]
    public async Task Request_ToolWithoutSchema_GetsDefaultParameters()
    {
        var (client, handler) = Sut();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")],
            new ChatOptions { Tools = [new SchemaLessTool()] });

        Assert.Contains("\"parameters\":{\"type\":\"object\",\"properties\":{}}", handler.LastBody!);
    }
}
