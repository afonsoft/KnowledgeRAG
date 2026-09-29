using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Backing service for /api/settings/assistant (SPEC-20260929-a2a-assistant-
/// delegation RF-001): describes/saves/clears the low-cost assistant provider
/// and tests connectivity. The API key lives in the secret store under
/// <see cref="IntegrationProviders.Assistant"/> — DTOs only ever carry
/// <c>hasKey</c> + a masked hint.
/// </summary>
public interface IAssistantSettingsService
{
    Task<AssistantSettingsDto> DescribeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default);
    Task RemoveKeyAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<TestChatConnectionResponse> TestAsync(
        TestAssistantConnectionRequest request, CancellationToken cancellationToken = default);
}
