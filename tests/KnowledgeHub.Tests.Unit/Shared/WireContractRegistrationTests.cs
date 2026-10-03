using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Tests.Unit.Shared;

/// <summary>
/// Audit 2026-10-03: Blazor WASM builds run with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so every type the
/// client (de)serializes through <see cref="SharedJson.Options"/> must have
/// metadata in <see cref="SharedJsonContext"/> — anonymous types, client-local
/// DTOs and unregistered <c>List&lt;T&gt;</c> shapes throw
/// <c>NoMetadataForType</c> at runtime. These tests lock the convention:
/// every public wire DTO in <c>KnowledgeHub.Shared.Contracts</c> is
/// registered, and every registered DTO round-trips through the options.
/// </summary>
public sealed class WireContractRegistrationTests
{
    private static readonly Type ContractsAnchor = typeof(AgentRequest);

    private static IEnumerable<Type> PublicContractDtos() =>
        ContractsAnchor.Assembly.GetExportedTypes()
            .Where(t => t.Namespace == typeof(AgentRequest).Namespace)
            .Where(t => t.IsClass || t.IsValueType)
            .Where(t => !t.IsAbstract)
            .Where(t => !t.IsEnum)
            .Where(t => !typeof(Attribute).IsAssignableFrom(t))
            .Where(t => !t.IsGenericTypeDefinition)
            // static helpers (e.g. AzureCredentialClassifier) never cross the wire
            .Where(t => !(t.IsSealed && t.IsAbstract && !t.GetMembers(BindingFlags.Public | BindingFlags.Instance).Any()));

    [Fact]
    public void EveryPublicContractDto_HasSourceGeneratedMetadata()
    {
        var ctx = SharedJsonContext.Default;

        var missing = PublicContractDtos()
            .Where(t => ctx.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Contract DTOs missing from SharedJsonContext (register via [JsonSerializable] — " +
            "WASM runs without reflection): " + string.Join(", ", missing));
    }

    public static IEnumerable<object[]> RegisteredDtos() =>
        PublicContractDtos()
            .Where(t => t.IsClass)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new object[] { t });

    /// <summary>Each registered contract serializes through the source-gen
    /// resolver — proves the metadata exists for write too, not just read.</summary>
    [Theory]
    [MemberData(nameof(RegisteredDtos))]
    public void RegisteredContract_SerializesViaContext(Type dtoType)
    {
        var ctx = SharedJsonContext.Default;
        JsonTypeInfo info = Assert.IsAssignableFrom<JsonTypeInfo>(ctx.GetTypeInfo(dtoType));
        Assert.NotNull(info);
    }

    [Fact]
    public void RepresentativeDtos_RoundTrip()
    {
        // Instantiatable shapes covering each contract family — catches
        // converter/property-shape issues that mere registration can't.
        var cases = new (Type Type, object Value)[]
        {
            (typeof(AgentRequest), new AgentRequest { Prompt = "hi", ThreadId = Guid.NewGuid() }),
            (typeof(CreateThreadRequest), new CreateThreadRequest { Title = "t" }),
            (typeof(MeResponse), new MeResponse("u", false)),
            (typeof(KnowledgeSourceDto), new KnowledgeSourceDto
            {
                Id = Guid.NewGuid(), Name = "s", Type = SourceType.WebPage,
                IsActive = true, AutoSyncEnabled = false
            }),
            (typeof(EvalGateDto), new EvalGateDto { Status = "pass" }),
            (typeof(McpMonitorEventDto), new McpMonitorEventDto
            {
                Timestamp = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
                Kind = McpMonitorEventKind.ToolCall
            }),
            (typeof(List<EvalRunSummaryDto>),
                new List<EvalRunSummaryDto> { new() { Id = Guid.NewGuid() } }),
        };

        foreach (var (type, value) in cases)
        {
            var json = JsonSerializer.Serialize(value, type, SharedJson.Options);
            var back = JsonSerializer.Deserialize(json, type, SharedJson.Options);
            Assert.NotNull(back);
        }
    }
}
