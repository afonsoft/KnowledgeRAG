using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Chat;
using Microsoft.Extensions.AI;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Gateways OpenAI-compatible restritos (ex.: OmniRoute) rejeitam campos nulos
// ("temperature: must be a number" → HTTP 400). O request sempre sai com
// valores default: temperature 1.0, max_tokens 4096, tools [] e, por mensagem,
// tool_calls [] / tool_call_id "". Tool sem schema recebe parameters objeto vazio.
public sealed class OpenAiChatClientTests : IDisposable
{
    private readonly List<IDisposable> _owned = new();
    /// <summary>Handler fake que captura o corpo da requisição e responde um completion mínimo.</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        /// <summary>Corpo JSON da última requisição capturada.</summary>
        public string? LastBody { get; private set; }

        /// <summary>Lê o corpo do request e devolve uma resposta OpenAI mínima.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""",
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

    /// <summary>Monta um OpenAiChatClient apontando para o handler de captura.</summary>
    private (OpenAiChatClient Client, CaptureHandler Handler) Sut(ChatProviderOptions? options = null)
    {
        var handler = new CaptureHandler();
        var http = new HttpClient(handler);
        _owned.Add(http);
        _owned.Add(handler);
        var client = new OpenAiChatClient(http, options ?? new ChatProviderOptions
        {
            Provider = "openai",
            Endpoint = "https://gw.test",
            Model = "m"
        });
        return (client, handler);
    }

    /// <summary>Sem temperature/max_tokens configurados, o JSON sai com os defaults.</summary>
    [Fact]
    public async Task Request_SendsDefaults_WhenNotConfigured()
    {
        var (client, handler) = Sut();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")]);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"temperature\":1", handler.LastBody!);
        Assert.Contains("\"max_tokens\":4096", handler.LastBody!);
        Assert.Contains("\"tools\":[]", handler.LastBody!);
        Assert.DoesNotContain("null", handler.LastBody!);
    }

    /// <summary>Com temperature/max_tokens configurados, o JSON sai com os valores informados.</summary>
    [Fact]
    public async Task Request_IncludesConfiguredFields()
    {
        var (client, handler) = Sut(new ChatProviderOptions
        {
            Provider = "openai",
            Endpoint = "https://gw.test",
            Model = "m",
            Temperature = 0.5,
            MaxTokens = 128
        });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")]);

        Assert.Contains("\"temperature\":0.5", handler.LastBody);
        Assert.Contains("\"max_tokens\":128", handler.LastBody);
    }

    /// <summary>Valores por chamada (ChatOptions) sobrepõem os defaults das options.</summary>
    [Fact]
    public async Task Request_ChatOptionsBeatDefaults()
    {
        var (client, handler) = Sut();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")],
            new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64 });

        Assert.Contains("\"temperature\":0.2", handler.LastBody);
        Assert.Contains("\"max_tokens\":64", handler.LastBody);
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

    /// <summary>Tool call sem arguments serializa "{}" e tool result carrega o callId.</summary>
    [Fact]
    public async Task Request_ToolCallWithoutArguments_DefaultsToEmptyObject()
    {
        var (client, handler) = Sut();
        var assistant = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "search_knowledge")]);
        var toolResult = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call-1", "resultado")]);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi"), assistant, toolResult]);

        Assert.Contains("\"id\":\"call-1\"", handler.LastBody!);
        Assert.Contains("\"arguments\":\"{}\"", handler.LastBody!);
        Assert.Contains("\"tool_call_id\":\"call-1\"", handler.LastBody!);
        Assert.DoesNotContain("null", handler.LastBody!);
    }

    /// <summary>Tool sem schema recebe parameters {"type":"object","properties":{}}.</summary>
    [Fact]
    public async Task Request_ToolWithoutSchema_GetsDefaultParameters()
    {
        var (client, handler) = Sut();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "oi")],
            new ChatOptions { Tools = [new SchemaLessTool()] });

        Assert.Contains("\"name\":\"schema_less\"", handler.LastBody!);
        Assert.Contains("\"parameters\":{\"type\":\"object\",\"properties\":{}}", handler.LastBody!);
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var d in _owned)
            d.Dispose();
    }

}