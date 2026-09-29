using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/settings (SPEC-20260916-firecrawl-mcp-proxy).</summary>
public sealed class SettingsApiClient(HttpClient http)
{
    /// <summary>Lista as integrações com o status mascarado de cada key.</summary>
    public Task<IntegrationSettingsResponse?> ListIntegrationsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<IntegrationSettingsResponse>("api/settings/integrations", ct);

    /// <summary>Salva/substitui a API key de uma integração.</summary>
    public async Task<ApiResult<object>> SetKeyAsync(string provider, string apiKey, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync(
            $"api/settings/integrations/{Uri.EscapeDataString(provider)}",
            new SetIntegrationKeyRequest { ApiKey = apiKey }, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Liga/desliga a integração — desligada some do catálogo MCP.</summary>
    public async Task<ApiResult<object>> SetEnabledAsync(string provider, bool enabled, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync(
            $"api/settings/integrations/{Uri.EscapeDataString(provider)}/enabled",
            new SetIntegrationEnabledRequest { Enabled = enabled }, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove a key armazenada de uma integração.</summary>
    public async Task<ApiResult<object>> RemoveKeyAsync(string provider, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/settings/integrations/{Uri.EscapeDataString(provider)}", ct);
        return await ReadAsync<object>(response, ct);
    }

    // SPEC-20260916-settings-chat-config: chat provider card endpoints.

    /// <summary>Obtém a configuração efetiva de chat (mascarada, sem a key).</summary>
    public Task<ChatSettingsDto?> GetChatAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<ChatSettingsDto>("api/settings/chat", ct);

    /// <summary>Salva endpoint/model do chat e, se informada, a API key.</summary>
    public async Task<ApiResult<object>> SaveChatAsync(string endpoint, string model, string? apiKey, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/settings/chat",
            new SaveChatSettingsRequest { Endpoint = endpoint, Model = model, ApiKey = apiKey }, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove apenas a key de chat armazenada (env volta a valer).</summary>
    public async Task<ApiResult<object>> RemoveChatKeyAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/chat/apikey", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Apaga a configuração persistida de chat, voltando às variáveis de ambiente.</summary>
    public async Task<ApiResult<object>> ClearChatAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/chat", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Testa a conexão com o provider usando os valores informados, sem persistir nada.</summary>
    public async Task<ApiResult<TestChatConnectionResponse>> TestChatAsync(TestChatConnectionRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/settings/chat/test", request, ct);
        return await ReadAsync<TestChatConnectionResponse>(response, ct);
    }

    // SPEC-20260929-a2a-assistant-delegation RF-001: low-cost assistant provider.

    /// <summary>Obtém a configuração efetiva do assistente (mascarada, sem a key).</summary>
    public Task<AssistantSettingsDto?> GetAssistantAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<AssistantSettingsDto>("api/settings/assistant", ct);

    /// <summary>Salva enabled/mode/endpoint/model/route do assistente; key em branco mantém a atual.</summary>
    public async Task<ApiResult<object>> SaveAssistantAsync(SaveAssistantSettingsRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/settings/assistant", request, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove apenas a key do assistente armazenada.</summary>
    public async Task<ApiResult<object>> RemoveAssistantKeyAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/assistant/apikey", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Apaga a configuração persistida do assistente, voltando ao ambiente.</summary>
    public async Task<ApiResult<object>> ClearAssistantAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/assistant", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Testa a conexão (local: /v1/models; remote: resolve o Agent Card).</summary>
    public async Task<ApiResult<TestChatConnectionResponse>> TestAssistantAsync(TestAssistantConnectionRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/settings/assistant/test", request, ct);
        return await ReadAsync<TestChatConnectionResponse>(response, ct);
    }

    // SPEC-20260923-graph-settings-ui: GraphRAG switch + tuning knobs.

    /// <summary>Obtém a configuração efetiva do grafo (store → env/defaults).</summary>
    public Task<GraphSettingsDto?> GetGraphAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<GraphSettingsDto>("api/settings/graph", ct);

    /// <summary>Salva a configuração do grafo; efeito imediato, sem restart.</summary>
    public async Task<ApiResult<object>> SaveGraphAsync(SaveGraphSettingsRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/settings/graph", request, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Apaga a configuração persistida do grafo, voltando às variáveis de ambiente.</summary>
    public async Task<ApiResult<object>> ClearGraphAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/graph", ct);
        return await ReadAsync<object>(response, ct);
    }

    // SPEC-20260928-resilience-tool-fallback-wiring RF-004: fallback policy (mode/budget/alternates).

    /// <summary>Obtém a configuração efetiva de resiliência (store → env/defaults).</summary>
    public Task<ResilienceSettingsDto?> GetResilienceAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<ResilienceSettingsDto>("api/settings/resilience", ct);

    /// <summary>Salva a política de fallback; efeito imediato, sem restart.</summary>
    public async Task<ApiResult<object>> SaveResilienceAsync(SaveResilienceSettingsRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/settings/resilience", request, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Apaga a política persistida, voltando às chaves Resilience:Fallback.</summary>
    public async Task<ApiResult<object>> ClearResilienceAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/resilience", ct);
        return await ReadAsync<object>(response, ct);
    }

    // SPEC-20260916-api-key-settings: per-API-key chat provider settings.

    /// <summary>Obtém a configuração efetiva de chat para uma API key específica.</summary>
    public Task<ApiKeyChatSettingsDto?> GetApiKeyChatAsync(Guid apiKeyId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<ApiKeyChatSettingsDto>($"api/api-keys/{apiKeyId}/settings/chat", ct);

    /// <summary>Salva endpoint/model/chat key para uma API key específica.</summary>
    public async Task<ApiResult<object>> SaveApiKeyChatAsync(Guid apiKeyId, string? endpoint, string? model, string? apiKey, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/api-keys/{apiKeyId}/settings/chat",
            new SaveApiKeyChatSettingsRequest { Endpoint = endpoint, Model = model, ApiKey = apiKey }, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove o override de chat de uma API key.</summary>
    public async Task<ApiResult<object>> RemoveApiKeyChatAsync(Guid apiKeyId, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/api-keys/{apiKeyId}/settings/chat", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Salva/substitui a key de uma integração para uma API key específica
    /// (override por chave — em branco a chave usa a key global da integração).</summary>
    public async Task<ApiResult<object>> SetApiKeyIntegrationKeyAsync(Guid apiKeyId, string provider, string apiKey, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync(
            $"api/api-keys/{apiKeyId}/settings/integrations/{Uri.EscapeDataString(provider)}",
            new SetIntegrationKeyRequest { ApiKey = apiKey }, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove a key de integração sobreposta de uma API key — volta à global.</summary>
    public async Task<ApiResult<object>> RemoveApiKeyIntegrationKeyAsync(Guid apiKeyId, string provider, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync(
            $"api/api-keys/{apiKeyId}/settings/integrations/{Uri.EscapeDataString(provider)}", ct);
        return await ReadAsync<object>(response, ct);
    }

    // SPEC-20260924-redis-cache-and-tool-caching: cache inspection and clear.

    /// <summary>Obtém estatísticas e chaves ativas do cache.</summary>
    public Task<CacheStatsDto?> GetCacheStatsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<CacheStatsDto>("api/settings/cache", ct);

    // SPEC-20260926-settings-tabs-database-metrics RF-003.

    /// <summary>Métricas do banco: provider, tamanhos, contagens por entidade, vector store.</summary>
    public Task<DatabaseStatsDto?> GetDatabaseStatsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<DatabaseStatsDto>("api/settings/database", ct);

    // SPEC-20260925-runtime-log-level RF-003/RF-004: runtime log level.

    /// <summary>Nível de log atual + auto-reset.</summary>
    public Task<LogLevelState?> GetLogLevelAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<LogLevelState>("api/settings/log-level", ct);

    /// <summary>Define o nível; <paramref name="minutes"/> agenda auto-reset.</summary>
    public async Task<ApiResult<LogLevelState>> SetLogLevelAsync(string level, int minutes, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/settings/log-level",
            new { level, minutes }, ct);
        return await ReadAsync<LogLevelState>(response, ct);
    }

    // SPEC-20260926-settings-ux-embeddings RF-004.

    /// <summary>Estado efetivo do provider de embeddings (store + env mascarado).</summary>
    public Task<EmbeddingSettingsDto?> GetEmbeddingsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<EmbeddingSettingsDto>("api/settings/embeddings", ct);

    /// <summary>Salva provider/endpoint/model/dims/chunking de embeddings; key em branco mantém.</summary>
    public async Task<ApiResult<object>> SaveEmbeddingsAsync(SaveEmbeddingSettingsRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/settings/embeddings", request, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove só a key persistida de embeddings.</summary>
    public async Task<ApiResult<object>> RemoveEmbeddingsKeyAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/embeddings/apikey", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Apaga o override de embeddings — env volta a valer.</summary>
    public async Task<ApiResult<object>> ClearEmbeddingsAsync(CancellationToken ct = default)
    {
        var response = await http.DeleteAsync("api/settings/embeddings", ct);
        return await ReadAsync<object>(response, ct);
    }

    // SPEC-20260926-settings-ux-embeddings RF-003.

    /// <summary>Remove uma entrada específica do cache (L1+L2 + tracked set).</summary>
    public async Task<ApiResult<object>> DeleteCacheKeyAsync(string key, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/settings/cache/keys/{Uri.EscapeDataString(key)}", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Limpa todas as chaves do cache no backend ativo.</summary>
    public async Task<ApiResult<ClearCacheResultDto>> ClearCacheAsync(CancellationToken ct = default)
    {
        var response = await http.PostAsync("api/settings/cache/clear", null, ct);
        return await ReadAsync<ClearCacheResultDto>(response, ct);
    }

    /// <summary>Lê a resposta HTTP num ApiResult: desserializa o corpo em sucesso
    /// e extrai o campo "error" (ou o status HTTP) em falha.</summary>
    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            T? value = default;
            try { value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct); }
            catch { /* 204 or non-JSON body */ }
            return new ApiResult<T>(value, null);
        }

        string? error = null;
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(cancellationToken: ct);
            if (doc is not null && doc.TryGetValue("error", out var e))
                error = e.GetString();
        }
        catch { /* non-JSON error body */ }

        return new ApiResult<T>(default, error ?? $"HTTP {(int)response.StatusCode}");
    }
}

/// <summary>GET/PUT /api/settings/log-level payload (SPEC-20260925-runtime-log-level).</summary>
public sealed class LogLevelState
{
    public string Level { get; set; } = "Information";
    public string? ConfiguredDefault { get; set; }
    public DateTimeOffset? AutoResetAt { get; set; }
}
