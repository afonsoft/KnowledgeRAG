using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Shared;

/// <summary>
/// Audit 2026-10-03 (P5): source-generated STJ metadata for the wire
/// contracts. Registered first in the resolver chain on both ends
/// (server: <c>ConfigureHttpJsonOptions</c>, client: <c>SharedJson.Options</c>);
/// types not listed here transparently fall back to reflection.
///
/// Quirk to respect: source-gen deserialization assigns <c>default</c> to
/// init-only properties that are absent from the JSON — their C# initializer
/// is NOT applied (reflection honors it). Wire contracts must therefore use
/// <c>set</c> (not <c>init</c>) on any member that has a default value, e.g.
/// <c>IsActive { get; set; } = true</c>. See CreateKnowledgeSourceRequest.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
// Agent + threads + approvals (chat hot path)
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
[JsonSerializable(typeof(AgentMessage))]
[JsonSerializable(typeof(AgentStep))]
[JsonSerializable(typeof(ResumeAgentRequest))]
[JsonSerializable(typeof(ThreadDto))]
[JsonSerializable(typeof(ThreadMessageDto))]
[JsonSerializable(typeof(ThreadDetailDto))]
[JsonSerializable(typeof(CreateThreadRequest))]
[JsonSerializable(typeof(RenameThreadRequest))]
[JsonSerializable(typeof(PostThreadMessageRequest))]
[JsonSerializable(typeof(List<ThreadDto>))]
[JsonSerializable(typeof(ApprovalDto))]
[JsonSerializable(typeof(ApproveApprovalRequest))]
[JsonSerializable(typeof(List<ApprovalDto>))]
// Ask + search
[JsonSerializable(typeof(AskRequest))]
[JsonSerializable(typeof(AskResponse))]
[JsonSerializable(typeof(CitationDto))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(SearchResultItem))]
[JsonSerializable(typeof(SearchScoreBreakdown))]
[JsonSerializable(typeof(SearchFilter))]
[JsonSerializable(typeof(SearchMinScores))]
// Auth + api keys
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(ApiKeyDto))]
[JsonSerializable(typeof(List<ApiKeyDto>))]
[JsonSerializable(typeof(ApiKeyCreatedDto))]
[JsonSerializable(typeof(ApiKeySecretDto))]
[JsonSerializable(typeof(ApiKeyUsageDto))]
[JsonSerializable(typeof(ApiKeyUsageEventDto))]
[JsonSerializable(typeof(SetApiKeyRateLimitRequest))]
[JsonSerializable(typeof(SetApiKeyScopesRequest))]
[JsonSerializable(typeof(SetApiKeyWriteAccessRequest))]
// Sources + tools + monitor
[JsonSerializable(typeof(KnowledgeSourceDto))]
[JsonSerializable(typeof(List<KnowledgeSourceDto>))]
[JsonSerializable(typeof(CreateKnowledgeSourceRequest))]
[JsonSerializable(typeof(UpdateKnowledgeSourceRequest))]
[JsonSerializable(typeof(KnowledgeDocumentDto))]
[JsonSerializable(typeof(List<KnowledgeDocumentDto>))]
[JsonSerializable(typeof(SyncResultDto))]
[JsonSerializable(typeof(ToolDescriptorDto))]
[JsonSerializable(typeof(ToolListResponse))]
[JsonSerializable(typeof(McpMonitorEventDto))]
[JsonSerializable(typeof(LiveToolExecution))]
// Settings
[JsonSerializable(typeof(ChatSettingsDto))]
[JsonSerializable(typeof(SaveChatSettingsRequest))]
[JsonSerializable(typeof(TestChatConnectionRequest))]
[JsonSerializable(typeof(TestChatConnectionResponse))]
[JsonSerializable(typeof(ProviderModelsResponse))]
[JsonSerializable(typeof(ApiKeyChatSettingsDto))]
[JsonSerializable(typeof(ApiKeyIntegrationKeyDto))]
[JsonSerializable(typeof(SaveApiKeyChatSettingsRequest))]
[JsonSerializable(typeof(EmbeddingSettingsDto))]
[JsonSerializable(typeof(SaveEmbeddingSettingsRequest))]
[JsonSerializable(typeof(GraphSettingsDto))]
[JsonSerializable(typeof(SaveGraphSettingsRequest))]
[JsonSerializable(typeof(ResilienceSettingsDto))]
[JsonSerializable(typeof(ChatFallbackOptionDto))]
[JsonSerializable(typeof(SaveResilienceSettingsRequest))]
[JsonSerializable(typeof(IntegrationSettingsDto))]
[JsonSerializable(typeof(IntegrationSettingsResponse))]
[JsonSerializable(typeof(SetIntegrationKeyRequest))]
[JsonSerializable(typeof(SetIntegrationEnabledRequest))]
[JsonSerializable(typeof(AssistantSettingsDto))]
[JsonSerializable(typeof(SaveAssistantSettingsRequest))]
[JsonSerializable(typeof(TestAssistantConnectionRequest))]
[JsonSerializable(typeof(CacheStatsDto))]
[JsonSerializable(typeof(CacheKeyItemDto))]
[JsonSerializable(typeof(ClearCacheResultDto))]
// Graph + stats
[JsonSerializable(typeof(GraphNodeDto))]
[JsonSerializable(typeof(GraphEdgeDto))]
[JsonSerializable(typeof(GraphEpisodeDto))]
[JsonSerializable(typeof(GraphNodesResponse))]
[JsonSerializable(typeof(DatabaseStatsDto))]
[JsonSerializable(typeof(TableCountDto))]
public partial class SharedJsonContext : JsonSerializerContext;

/// <summary>
/// Web-default options with <see cref="SharedJsonContext"/> first in the
/// resolver chain and reflection as fallback — a safe drop-in for any call
/// site (registered DTOs get source-gen metadata, the rest keeps working).
/// </summary>
public static class SharedJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.TypeInfoResolverChain.Insert(0, SharedJsonContext.Default);
        return o;
    }
}
