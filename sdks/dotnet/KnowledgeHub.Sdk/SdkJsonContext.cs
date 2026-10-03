using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeHub.Sdk;

/// <summary>Serializer options shared by the SDK (camelCase wire format).</summary>
public static class HubJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = SdkJsonContext.Default
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HubSearchHit))]
[JsonSerializable(typeof(HubSearchResult))]
[JsonSerializable(typeof(HubCitation))]
[JsonSerializable(typeof(HubAskAnswer))]
[JsonSerializable(typeof(HubAgentStep))]
[JsonSerializable(typeof(HubAgentResult))]
[JsonSerializable(typeof(HubAgentMessage))]
[JsonSerializable(typeof(HubAgentMessage[]))]
[JsonSerializable(typeof(SdkAskRequest))]
[JsonSerializable(typeof(SdkAgentRequest))]
[JsonSerializable(typeof(SseEnvelope))]
internal sealed partial class SdkJsonContext : JsonSerializerContext;

internal sealed record SdkAskRequest
{
    public required string Question { get; init; }
    public int? TopK { get; init; }
    public string? Mode { get; init; }
    public bool? Generate { get; init; }
}

internal sealed record SdkAgentRequest
{
    public string? Prompt { get; init; }
    public HubAgentMessage[]? Messages { get; init; }
    public int? MaxIterations { get; init; }
    public bool AllowWrite { get; init; }
}

/// <summary>Envelope the stream endpoints wrap each payload in: {"seq":n,"data":{...}}.</summary>
internal sealed record SseEnvelope
{
    public int Seq { get; init; }
    public JsonElement Data { get; init; }
}
