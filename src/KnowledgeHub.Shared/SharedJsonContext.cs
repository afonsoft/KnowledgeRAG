using System.Diagnostics.CodeAnalysis;
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
[JsonSerializable(typeof(BaselineRequest))]
[JsonSerializable(typeof(SetLogLevelRequest))]
[JsonSerializable(typeof(LogLevelState))]
// Eval (wire contracts shared by the eval endpoints and the Eval UI).
[JsonSerializable(typeof(EvalRunSummaryDto))]
[JsonSerializable(typeof(List<EvalRunSummaryDto>))]
[JsonSerializable(typeof(EvalReportDto))]
[JsonSerializable(typeof(EvalMetricsDto))]
[JsonSerializable(typeof(EvalLatencyDto))]
[JsonSerializable(typeof(EvalCaseDto))]
[JsonSerializable(typeof(EvalDeltaDto))]
[JsonSerializable(typeof(EvalGateDto))]
[JsonSerializable(typeof(EvalBaselineDto))]
[JsonSerializable(typeof(List<EvalBaselineDto>))]
// Agent flows (UI-defined flows — REST + MCP + WASM editor share these).
[JsonSerializable(typeof(FlowInputDto))]
[JsonSerializable(typeof(FlowStepDto))]
[JsonSerializable(typeof(List<FlowStepDto>))]
[JsonSerializable(typeof(FlowDefinitionDto))]
[JsonSerializable(typeof(FlowDto))]
[JsonSerializable(typeof(List<FlowDto>))]
[JsonSerializable(typeof(FlowDetailDto))]
[JsonSerializable(typeof(CreateFlowRequest))]
[JsonSerializable(typeof(UpdateFlowRequest))]
[JsonSerializable(typeof(FlowRunRequest))]
[JsonSerializable(typeof(ValidateFlowRequest))]
[JsonSerializable(typeof(FlowStepResultDto))]
[JsonSerializable(typeof(List<FlowStepResultDto>))]
[JsonSerializable(typeof(FlowRunResultDto))]
[JsonSerializable(typeof(FlowRunDto))]
[JsonSerializable(typeof(List<FlowRunDto>))]
// Ingestion jobs + SignalR monitor feed (deserialized on trimmed WASM).
[JsonSerializable(typeof(SyncJobEnqueueDto))]
[JsonSerializable(typeof(IngestionJobDto))]
[JsonSerializable(typeof(List<IngestionJobDto>))]
[JsonSerializable(typeof(RagEvaluationStats))]
[JsonSerializable(typeof(SessionOpenedEvent))]
[JsonSerializable(typeof(SessionClosedEvent))]
[JsonSerializable(typeof(IngestionProgressEventDto))]
[JsonSerializable(typeof(List<IngestionProgressEventDto>))]
// Hub Snapshot payload arrives as IReadOnlyList<McpMonitorEventDto>.
[JsonSerializable(typeof(List<McpMonitorEventDto>))]
[JsonSerializable(typeof(IReadOnlyList<McpMonitorEventDto>))]
// Playground streams tool args as a raw JSON object (Dictionary<string, JsonElement>);
// eval run + error bodies use open dictionaries too — reflection is off in trimmed WASM.
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Graph + stats
[JsonSerializable(typeof(GraphNodeDto))]
[JsonSerializable(typeof(GraphEdgeDto))]
[JsonSerializable(typeof(GraphEpisodeDto))]
[JsonSerializable(typeof(List<GraphNodeDto>))]
[JsonSerializable(typeof(List<GraphEdgeDto>))]
[JsonSerializable(typeof(List<GraphEpisodeDto>))]
[JsonSerializable(typeof(GraphNodesResponse))]
[JsonSerializable(typeof(DatabaseStatsDto))]
[JsonSerializable(typeof(TableCountDto))]
// Audit 2026-10-03: source-gen emits thousands of instrumented lines under this
// context — exclude it so the coverage ratchet measures hand-written code.
[ExcludeFromCodeCoverage]
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
