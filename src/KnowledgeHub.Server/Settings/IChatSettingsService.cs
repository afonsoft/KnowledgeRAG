using KnowledgeHub.Server.Chat;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Configuração efetiva do provider de chat e resolução do client em runtime
/// (SPEC-20260916-settings-chat-config RF-002/RF-003). Uma linha em
/// <c>ChatSettings</c> sobrepõe o env <c>Chat:*</c> para endpoint/model e
/// implica provider <c>openai</c>; a API key resolve store → env. Mutações
/// chamam <see cref="Invalidate"/> para o próximo <see cref="GetClient"/>
/// reconstruir o client — sem restart.
/// </summary>
public interface IChatSettingsService
{
    /// <summary>Retorna as options efetivas (store sobre env). Cacheado por snapshot.</summary>
    ChatProviderOptions GetEffectiveOptions();

    /// <summary>Retorna o client das options efetivas, ou null quando provider=none.
    /// Nunca lança por falta de config — consumidores mantêm tolerância a null.</summary>
    IChatClient? GetClient();

    /// <summary>Descarta o snapshot em cache; o próximo acesso relê o store.</summary>
    void Invalidate();

    /// <summary>Retorna o estado efetivo mascarado para GET /api/settings/chat — nunca o segredo.</summary>
    Task<ChatSettingsDto> DescribeAsync(CancellationToken cancellationToken = default);

    /// <summary>Faz upsert da linha única de ChatSettings; grava a key quando
    /// <paramref name="apiKey"/> não é vazia (vazio mantém a key armazenada).</summary>
    Task SaveAsync(string endpoint, string model, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>Remove apenas a key "chat" do store — a key de env volta a valer.</summary>
    Task RemoveKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Remove a linha ChatSettings E a key do store — fallback total para env.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Sonda GET {endpoint}/v1/models com a key resolvível; campos
    /// vazios do request caem na config efetiva. Não persiste nada.</summary>
    Task<TestChatConnectionResponse> TestAsync(TestChatConnectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lista os ids de modelos via GET {endpoint}/v1/models com a key
    /// efetiva; <paramref name="endpointOverride"/> (query) vence a config —
    /// a UI chama com o endpoint digitado no form. Não persiste nada.</summary>
    Task<ProviderModelsResponse> ListModelsAsync(string? endpointOverride, CancellationToken cancellationToken = default);
}
