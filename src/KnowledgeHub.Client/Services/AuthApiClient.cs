using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/auth + /api/apikeys (SPEC-20260914-auth-login RF-011/RF-012).</summary>
public sealed class AuthApiClient(HttpClient http)
{
    public async Task<ApiResult<LoginResponse>> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(username, password), SharedJson.Options, ct);
        return await ReadAsync<LoginResponse>(response, ct);
    }

    /// <summary>SPEC-20260926-ops-and-ui-polish: anonymous MCP capabilities —
    /// the login page only advertises legacy SSE when the transport serves it.</summary>
    public async Task<bool> LegacySseEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonElement>("api/mcp/capabilities", SharedJson.Options, ct);
            return doc.TryGetProperty("legacySse", out var v) && v.GetBoolean();
        }
        catch
        {
            return true; // probe failed — assume the default hybrid transport
        }
    }

    public async Task<MeResponse?> MeAsync(CancellationToken ct = default)
    {
        var response = await http.GetAsync("api/auth/me", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MeResponse>(SharedJson.Options, cancellationToken: ct)
            : null;
    }

    public async Task<ApiResult<object>> ChangePasswordAsync(string current, string next, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/change-password",
            new ChangePasswordRequest(current, next), SharedJson.Options, ct);
        return await ReadAsync<object>(response, ct);
    }

    public async Task<ApiResult<object>> LogoutAsync(CancellationToken ct = default)
    {
        var response = await http.PostAsync("api/auth/logout", null, ct);
        return await ReadAsync<object>(response, ct);
    }

    public Task<List<ApiKeyDto>?> ListKeysAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<ApiKeyDto>>("api/apikeys", SharedJson.Options, ct);

    public async Task<ApiResult<ApiKeyCreatedDto>> CreateKeyAsync(string name, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/apikeys", new CreateApiKeyRequest(name), SharedJson.Options, ct);
        return await ReadAsync<ApiKeyCreatedDto>(response, ct);
    }

    public async Task<ApiResult<object>> RevokeKeyAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/apikeys/{id}", ct);
        return await ReadAsync<object>(response, ct);
    }

    // SPEC-20260915-apikey-usage-audit RF-006: per-key usage summary + audit trail.
    public Task<ApiKeyUsageDto?> GetKeyUsageAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<ApiKeyUsageDto>($"api/apikeys/{id}/usage", SharedJson.Options, ct);

    // SPEC-20260924-api-key-reveal-and-copy RF-002: get revealed secret for a key.
    public Task<ApiKeySecretDto?> GetKeySecretAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<ApiKeySecretDto>($"api/apikeys/{id}/secret", SharedJson.Options, ct);

    // SPEC-20260923-per-key-rate-limits RF-004: per-key rate-limit override.

    /// <summary>Salva o override de rate limit da chave (null herda o global).</summary>
    public async Task<ApiResult<object>> SetKeyRateLimitAsync(Guid id, SetApiKeyRateLimitRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/api-keys/{id}/rate-limit", request, SharedJson.Options, ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Remove o override — a chave volta aos limites globais.</summary>
    public async Task<ApiResult<object>> ClearKeyRateLimitAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/api-keys/{id}/rate-limit", ct);
        return await ReadAsync<object>(response, ct);
    }

    /// <summary>Liga/desliga tools de escrita para a chave — desligada ela fica
    /// somente-leitura (tools não-readonly respondem "sem permissão").</summary>
    public async Task<ApiResult<object>> SetKeyWriteAccessAsync(Guid id, bool allowWrite, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/api-keys/{id}/write-access",
            new SetApiKeyWriteAccessRequest(allowWrite), SharedJson.Options, ct);
        return await ReadAsync<object>(response, ct);
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = response.StatusCode == System.Net.HttpStatusCode.NoContent
                ? default
                : await response.Content.ReadFromJsonAsync<T>(SharedJson.Options, cancellationToken: ct);
            return new ApiResult<T>(value, null);
        }

        string? error = null;
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(SharedJson.Options, cancellationToken: ct);
            if (doc is not null && doc.TryGetValue("error", out var e))
                error = e.GetString();
        }
        catch { /* non-JSON error body */ }

        return new ApiResult<T>(default, error ?? $"HTTP {(int)response.StatusCode}");
    }
}
