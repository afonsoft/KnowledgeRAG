using System.Net;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Ingestion.Connectors.Clients;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-audio-transcription-connector: AssemblyAI
// upload→submit→poll happy path, diarized/chaptered Markdown rendering,
// sha256 incremental skip, per-file fault tolerance and auth/HTTP failures.
public sealed class AudioTranscriptionConnectorTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), $"aud-{Guid.NewGuid():N}");

    public AudioTranscriptionConnectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteAudio(string name, string content = "RIFF-fake")
    {
        var p = Path.Join(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private const string TranscriptJson = """
        {"id":"job1","status":"completed","text":"hello world","audio_duration":125.0,
         "utterances":[
           {"speaker":"A","start":12000,"end":15000,"text":"Bom dia pessoal"},
           {"speaker":"B","start":35000,"end":38000,"text":"O conector já está pronto"}
         ],
         "chapters":[
           {"start":0,"headline":"Abertura","summary":"status dos conectores"}
         ]}
        """;

    private sealed class FakeApi
    {
        public int Uploads;
        public string? SeenKey;
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler;

        public HttpResponseMessage Route(HttpRequestMessage request)
        {
            request.Headers.TryGetValues("authorization", out var k);
            SeenKey = k?.FirstOrDefault();
            var path = request.RequestUri!.AbsolutePath;
            return Handler?.Invoke(request) ?? (path switch
            {
                "/v2/upload" => Uploads++ is var _
                    ? Json("""{"upload_url":"https://cdn.assemblyai.com/u/1"}""")
                    : Json(""),
                "/v2/transcript" => Json("""{"id":"job1"}"""),
                _ when path.StartsWith("/v2/transcript/") => Json(TranscriptJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class RouterHandler(FakeApi api) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(api.Route(request));
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeSecrets(string? key = null) : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(key);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(false);
    }

    private AudioTranscriptionConnector Sut(FakeApi api, string? key = "aai-key") =>
        new(new FakeFactory(new RouterHandler(api)), new FakeSecrets(key),
            NullLogger<AudioTranscriptionConnector>.Instance);

    private KnowledgeSource Source(object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "audio",
        SourceType = SourceType.AudioTranscription,
        ConfigurationJson = JsonSerializer.Serialize(configuration)
    };

    // AC-1: mp3 → diarized + chaptered Markdown.
    [Fact]
    public async Task Mp3_RendersSpeakersAndChapters()
    {
        WriteAudio("reuniao.mp3");
        var api = new FakeApi();
        var result = await Sut(api).FetchAsync(
            Source(new { folderPath = _dir }), CancellationToken.None);

        var doc = Assert.Single(result.Documents);
        Assert.Contains("## Capítulos", doc.TextContent);
        Assert.Contains("[00:00:00] Abertura", doc.TextContent);
        Assert.Contains("**[Speaker A - 00:00:12]**: Bom dia pessoal", doc.TextContent);
        Assert.Contains("**[Speaker B - 00:00:35]**: O conector já está pronto", doc.TextContent);
        Assert.StartsWith("audio:", doc.Fingerprint);
        Assert.EndsWith(":assemblyai", doc.Fingerprint);
    }

    // AC-2: same bytes → no upload.
    [Fact]
    public async Task UnchangedFile_SkipsUpload()
    {
        WriteAudio("reuniao.mp3");
        var api = new FakeApi();
        var sut = Sut(api);
        var source = Source(new { folderPath = _dir });

        var first = await sut.FetchAsync(source, CancellationToken.None);
        var uri = first.Documents[0].UriReference;
        api.Uploads = 0;

        var second = await sut.FetchAsync(source,
            new Dictionary<string, string> { [uri] = first.Documents[0].Fingerprint! },
            CancellationToken.None);
        Assert.Equal(0, api.Uploads);
        Assert.Empty(second.Documents[0].TextContent);
    }

    // AC-3: HTTP 500 on poll → FailedUris + warning, other files survive.
    [Fact]
    public async Task PollFailure_LandsInFailedUris()
    {
        WriteAudio("bad.mp3");
        WriteAudio("good.mp3");
        var submits = 0;
        var api = new FakeApi
        {
            Handler = req => req.RequestUri!.AbsolutePath switch
            {
                "/v2/upload" => Json("""{"upload_url":"https://cdn/u"}"""),
                "/v2/transcript" => Json($$"""{"id":"job{{++submits}}"}"""),
                "/v2/transcript/job1" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => Json(TranscriptJson)
            }
        };
        var result = await Sut(api).FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None);
        Assert.Single(result.Documents);
        Assert.NotNull(result.FailedUris);
        Assert.Single(result.FailedUris!);
        Assert.Single(result.Warnings);
        Assert.Contains(".mp3", result.Warnings[0]);
    }

    // Edge: silent audio → document with "no speech" note, not an error.
    [Fact]
    public async Task SilentAudio_EmitsEmptySpeechNote()
    {
        WriteAudio("silencio.mp3");
        var api = new FakeApi
        {
            Handler = req => req.RequestUri!.AbsolutePath switch
            {
                "/v2/upload" => Json("""{"upload_url":"https://cdn/u"}"""),
                "/v2/transcript" => Json("""{"id":"job1"}"""),
                _ => Json("""{"id":"job1","status":"completed","text":"","utterances":[],"chapters":[]}""")
            }
        };
        var result = await Sut(api).FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None);
        var doc = Assert.Single(result.Documents);
        Assert.Contains("Nenhuma fala detectada", doc.TextContent);
    }

    // RF-001: AssemblyAI needs a key — clean error, no upload.
    [Fact]
    public async Task MissingApiKey_FailsPerFile()
    {
        WriteAudio("reuniao.mp3");
        var api = new FakeApi();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api, key: null).FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None));
        Assert.Equal(0, api.Uploads);
    }

    // RF-002: same-speaker utterances within 10s merge into one block.
    [Fact]
    public void Renderer_MergesSameSpeakerWithin10s()
    {
        var utts = new List<TranscriptUtterance>
        {
            new("A", 0, 5000, "Parte um."),
            new("A", 8000, 12000, "Parte dois."),
            new("B", 30000, 33000, "Interrupção.")
        };
        var merged = AudioTranscriptionRenderer.Merge(utts);
        Assert.Equal(2, merged.Count);
        Assert.Equal("Parte um. Parte dois.", merged[0].Text);
    }

    // Fingerprint format: audio:{sha256}:{provider}.
    [Fact]
    public async Task Fingerprint_HasSpecFormat()
    {
        WriteAudio("x.mp3");
        var api = new FakeApi();
        var result = await Sut(api).FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None);
        Assert.Matches(@"^audio:[0-9A-F]{64}:assemblyai$", result.Documents[0].Fingerprint);
    }
}
