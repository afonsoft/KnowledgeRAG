using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeHub.Review.Config;

/// <summary>Shared System.Text.Json options for signal.json / run.json output.</summary>
internal static class SignalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
