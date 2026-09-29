using KnowledgeHub.Server.Resilience;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Runtime-editable resilience/fallback settings
/// (SPEC-20260928-resilience-tool-fallback-wiring RF-004). Effective values =
/// persisted row → <c>Resilience:Fallback</c> config → defaults.
/// </summary>
public interface IResilienceSettingsService
{
    /// <summary>Current effective options (cached snapshot; invalidated on save/clear).</summary>
    FallbackOptions GetEffective();
    Task<ResilienceSettingsDto> DescribeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SaveResilienceSettingsRequest request, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    /// <summary>Drops the cached snapshot — next read reloads from store/config.</summary>
    void Invalidate();
}
