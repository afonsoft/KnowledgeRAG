using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeHub.Server.Audit.Evidence;
using KnowledgeHub.Server.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20261001-a2a-task-durability: durable task store survives restarts
// (AC-1), streaming emits incremental working progress (AC-2), terminal
// transitions POST an HMAC-signed webhook with bounded retries (AC-3), writes
// carry origin frontmatter per channel (AC-4), and the Agent Card advertises
// conditional push + per-skill modes (AC-5).
public class A2aDurabilityTests
{
    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly bool _stubChat;
        private readonly bool _pushEnabled;
        private readonly bool _allowPrivateEgress;
        private readonly bool _deleteDb;

        public string DbPath { get; }
        public string Vault { get; }

        public Host(
            string? dbPath = null, bool stubChat = false, bool pushEnabled = true,
            bool allowPrivateEgress = false, bool deleteDb = true)
        {
            DbPath = dbPath ?? Path.Join(Path.GetTempPath(), $"kh-a2ad-{Guid.NewGuid():N}.db");
            Vault = Path.Join(Path.GetTempPath(), $"kh-vault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Vault);
            _stubChat = stubChat;
            _pushEnabled = pushEnabled;
            _allowPrivateEgress = allowPrivateEgress;
            _deleteDb = deleteDb;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["A2a:PushNotifications:Enabled"] = _pushEnabled ? "true" : "false",
                    ["Security:Egress:AllowPrivateNetworks"] = _allowPrivateEgress ? "true" : null,
                }));
            if (_stubChat)
            {
                builder.ConfigureServices(services =>
                    services.AddSingleton<IChatClient>(new ScriptedChatClient()));
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (_deleteDb)
            {
                try { File.Delete(DbPath); } catch (IOException) { /* best effort */ }
            }
            try { Directory.Delete(Vault, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task<string> NewKeyAsync(WebApplicationFactory<Program> factory, string name)
    {
        var admin = await TestAuth.LoginAsync(factory);
        return await TestAuth.CreateApiKeyAsync(admin, name);
    }

    private static HttpRequestMessage JsonRpc(object id, string method, object? @params = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/a2a");
        req.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        });
        return req;
    }

    private static object SendMessageParams(
        string text, string skill, IDictionary<string, object>? extraMetadata = null)
    {
        var metadata = new Dictionary<string, object> { ["skill"] = skill };
        if (extraMetadata is not null)
            foreach (var kv in extraMetadata) metadata[kv.Key] = kv.Value;
        return new
        {
            message = new
            {
                role = "ROLE_USER",
                messageId = Guid.NewGuid().ToString("N"),
                parts = new[] { new { text } },
                metadata
            }
        };
    }

    /// <summary>JSON-RPC <c>result</c> for Send* is the union
    /// <c>{task: {...}}</c>; task-returning methods serialize the task
    /// directly — normalize both.</summary>
    private static JsonElement ResultTask(JsonElement rpcBody)
    {
        var result = rpcBody.GetProperty("result");
        return result.TryGetProperty("task", out var task) ? task : result;
    }

    /// <summary>AC-1: a task persisted by one host instance is returned by a
    /// fresh host pointed at the same database file.</summary>
    [Fact]
    public async Task GetTask_SurvivesServerRestart()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"kh-a2ad-{Guid.NewGuid():N}.db");
        string taskId;
        string secret;
        try
        {
            await using (var host1 = new Host(dbPath, deleteDb: false))
            {
                secret = await NewKeyAsync(host1, "a2a-durable");
                using var client1 = host1.CreateClient();
                client1.DefaultRequestHeaders.Authorization = new("Bearer", secret);

                var res = await client1.SendAsync(
                    JsonRpc(1, "SendMessage", SendMessageParams("ping", "search_knowledge")));
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var body = await res.Content.ReadFromJsonAsync<JsonElement>();
                var sent = ResultTask(body);
                taskId = sent.GetProperty("id").GetString()!;
                Assert.Equal("TASK_STATE_COMPLETED",
                    sent.GetProperty("status").GetProperty("state").GetString());
            }

            // Release pooled SQLite handles before the second host opens the file.
            SqliteConnection.ClearAllPools();

            await using var host2 = new Host(dbPath);
            using var client2 = host2.CreateClient();
            client2.DefaultRequestHeaders.Authorization = new("Bearer", secret);

            var get = await client2.SendAsync(JsonRpc(2, "GetTask", new { id = taskId }));
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var persisted = await get.Content.ReadFromJsonAsync<JsonElement>();
            var task = ResultTask(persisted);
            Assert.Equal(taskId, task.GetProperty("id").GetString());
            Assert.Equal("TASK_STATE_COMPLETED",
                task.GetProperty("status").GetProperty("state").GetString());
            Assert.True(task.TryGetProperty("artifacts", out var artifacts)
                        && artifacts.ValueKind == JsonValueKind.Array);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>AC-2: agent_chat over SendStreamingMessage surfaces ≥2 working
    /// status events before the terminal state.</summary>
    [Fact]
    public async Task StreamingMessage_EmitsIncrementalWorkingProgress()
    {
        await using var host = new Host(stubChat: true);
        var secret = await NewKeyAsync(host, "a2a-stream");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var res = await client.SendAsync(
            JsonRpc(10, "SendStreamingMessage", SendMessageParams("do work", "agent_chat")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadAsStringAsync();
        var working = Regex.Matches(body, "\"state\"\\s*:\\s*\"TASK_STATE_WORKING\"").Count;
        Assert.True(working >= 2,
            $"expected ≥2 working events, got {working}. Body: {body}");
        Assert.Contains("\"state\":\"TASK_STATE_COMPLETED\"", body);
    }

    /// <summary>AC-3: a push config registered on an already-terminal task
    /// dispatches immediately with the HMAC signature; a failing endpoint is
    /// retried exactly 3× and abandoned.</summary>
    [Fact]
    public async Task PushNotification_SignedDelivery_AndBoundedRetry()
    {
        await using var host = new Host(allowPrivateEgress: true);
        var secret = await NewKeyAsync(host, "a2a-push");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var received = System.Threading.Channels.Channel
            .CreateUnbounded<(string Path, string? Signature, string? Token, string Body)>();
        var loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    received.Writer.TryWrite((
                        ctx.Request.Url!.AbsolutePath,
                        ctx.Request.Headers["X-KH-Signature"],
                        ctx.Request.Headers["X-A2A-Notification-Token"],
                        body));
                    ctx.Response.StatusCode =
                        ctx.Request.Url.AbsolutePath == "/fail" ? 500 : 200;
                    ctx.Response.Close();
                }
                catch (Exception) when (!listener.IsListening) { }
                catch (HttpListenerException) { }
            }
        });
        try
        {
            var send = await client.SendAsync(
                JsonRpc(1, "SendMessage", SendMessageParams("ping", "search_knowledge")));
            var taskId = ResultTask(await send.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            var create = await client.SendAsync(JsonRpc(2, "CreateTaskPushNotificationConfig", new
            {
                taskId,
                configId = "cfg-ok",
                config = new { url = $"http://127.0.0.1:{port}/ok", token = "tok-1" }
            }));
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(created.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object,
                $"push config create failed: {created.GetRawText()}");

            var hit = await received.Reader.ReadAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            Assert.Equal("/ok", hit.Path);
            Assert.Equal("tok-1", hit.Token);
            Assert.Contains(taskId, hit.Body);
            Assert.NotNull(hit.Signature);

            // Verify the signature against the evidence master key.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var secrets = scope.ServiceProvider.GetRequiredService<IIntegrationSecretStore>();
                var keyHex = await secrets.GetAsync(EvidenceChainService.SecretSlot);
                Assert.NotNull(keyHex);
                using var hmac = new HMACSHA256(Convert.FromHexString(keyHex!));
                var expected = "hmac-sha256:" + Convert.ToHexString(
                    hmac.ComputeHash(Encoding.UTF8.GetBytes(hit.Body))).ToLowerInvariant();
                Assert.Equal(expected, hit.Signature);
            }

            // Failing endpoint → exactly 3 attempts (SPEC AC-3 bounded retry).
            var failCreate = await client.SendAsync(JsonRpc(3, "CreateTaskPushNotificationConfig", new
            {
                taskId,
                configId = "cfg-fail",
                config = new { url = $"http://127.0.0.1:{port}/fail" }
            }));
            Assert.Equal(HttpStatusCode.OK, failCreate.StatusCode);

            var attempts = 0;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (attempts < 3)
            {
                var req = await received.Reader.ReadAsync(deadline.Token);
                if (req.Path == "/fail") attempts++;
            }
            Assert.Equal(3, attempts);

            // No 4th attempt — give it a beat longer than the last backoff.
            var extra = await Task.WhenAny(
                received.Reader.ReadAsync(deadline.Token).AsTask(),
                Task.Delay(TimeSpan.FromSeconds(4)));
            if (extra is Task<(string Path, string?, string?, string)> done
                && done.IsCompletedSuccessfully)
            {
                Assert.NotEqual("/fail", (await done).Path);
            }
        }
        finally
        {
            listener.Stop();
            try { await loop; } catch { /* listener stopped */ }
        }
    }

    /// <summary>AC-4: write_knowledge over A2A stamps channel "a2a" + caller
    /// key + agent name; the same tool over the REST/MCP surface stamps "mcp".</summary>
    [Fact]
    public async Task WriteKnowledge_StampsChannelOrigin()
    {
        await using var host = new Host();
        var admin = await TestAuth.LoginAsync(host);
        var create = await admin.PostAsJsonAsync("/api/sources", new
        {
            name = $"Vault{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = host.Vault },
            isActive = true
        });
        create.EnsureSuccessStatusCode();

        var secret = await TestAuth.CreateApiKeyAsync(admin, "a2a-writer");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var a2a = await client.SendAsync(JsonRpc(1, "SendMessage",
            SendMessageParams("ignored", "write_knowledge", new Dictionary<string, object>
            {
                ["agentName"] = "TestAgent",
                ["arguments"] = new Dictionary<string, object>
                {
                    ["title"] = "A2A Written",
                    ["content"] = "a2a body text"
                }
            })));
        Assert.Equal(HttpStatusCode.OK, a2a.StatusCode);
        var a2aBody = await a2a.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(a2aBody.TryGetProperty("error", out var rpcErr)
                     && rpcErr.ValueKind == JsonValueKind.Object,
            $"A2A write_knowledge failed: {a2aBody.GetRawText()}");

        // MCP-channel write through the same catalog handler.
        var mcp = await client.PostAsJsonAsync("/api/tools/write_knowledge", new
        {
            title = "Mcp Written",
            content = "mcp body text"
        });
        mcp.EnsureSuccessStatusCode();

        var a2aFile = Directory.EnumerateFiles(host.Vault, "*.md", SearchOption.AllDirectories)
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .FirstOrDefault(x => x.Text.Contains("a2a body text"));
        var mcpFile = Directory.EnumerateFiles(host.Vault, "*.md", SearchOption.AllDirectories)
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .FirstOrDefault(x => x.Text.Contains("mcp body text"));

        Assert.False(a2aFile == default, "A2A write_knowledge produced no vault file");
        Assert.False(mcpFile == default, "MCP write_knowledge produced no vault file");
        Assert.Contains("channel: \"a2a\"", a2aFile.Text);
        Assert.Contains("agentName: \"TestAgent\"", a2aFile.Text);
        Assert.Contains("keyId:", a2aFile.Text);
        Assert.Contains("at:", a2aFile.Text);
        Assert.Contains("channel: \"mcp\"", mcpFile.Text);
    }

    /// <summary>AC-5: the Agent Card advertises push capability only when the
    /// feature flag is on, and every skill declares input/output modes.</summary>
    [Fact]
    public async Task AgentCard_ConditionalPush_AndSkillModes()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var card = await client.GetFromJsonAsync<JsonElement>("/.well-known/agent-card.json");

        Assert.True(card.GetProperty("capabilities")
            .GetProperty("pushNotifications").GetBoolean());
        var skills = card.GetProperty("skills").EnumerateArray().ToList();
        var readDoc = skills.First(s => s.GetProperty("id").GetString() == "read_document");
        Assert.Contains("application/json",
            readDoc.GetProperty("inputModes").EnumerateArray().Select(m => m.GetString()));
        var chat = skills.First(s => s.GetProperty("id").GetString() == "agent_chat");
        Assert.Contains("text/plain",
            chat.GetProperty("inputModes").EnumerateArray().Select(m => m.GetString()));
        foreach (var skill in skills)
        {
            Assert.True(skill.TryGetProperty("inputModes", out var im)
                        && im.GetArrayLength() > 0,
                $"skill {skill.GetProperty("id")} missing inputModes");
            Assert.True(skill.TryGetProperty("outputModes", out var om)
                        && om.GetArrayLength() > 0,
                $"skill {skill.GetProperty("id")} missing outputModes");
            Assert.True(skill.TryGetProperty("examples", out var ex)
                        && ex.GetArrayLength() > 0,
                $"skill {skill.GetProperty("id")} missing examples");
        }
    }

    /// <summary>AC-5 counterpart: with push disabled the card drops the
    /// capability and the CRUD surface returns the SDK's unsupported error.</summary>
    [Fact]
    public async Task PushNotifications_Disabled_CardOmitsCapability_AndCrudRejected()
    {
        await using var host = new Host(pushEnabled: false);
        using var anon = host.CreateClient();
        var card = await anon.GetFromJsonAsync<JsonElement>("/.well-known/agent-card.json");
        var caps = card.GetProperty("capabilities");
        Assert.True(!caps.TryGetProperty("pushNotifications", out var push)
                    || !push.GetBoolean());

        var secret = await NewKeyAsync(host, "a2a-push-off");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        var res = await client.SendAsync(JsonRpc(1, "CreateTaskPushNotificationConfig", new
        {
            taskId = "t1",
            configId = "cfg-1",
            config = new { url = "https://example.com/hook" }
        }));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.Object,
            $"expected JSON-RPC error, got: {body.GetRawText()}");
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Answers agent_chat with one search_knowledge tool call, then a
    /// final text — enough to produce ≥3 progress events across 2 iterations.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            ChatMessage message = call == 1
                ? new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "search_knowledge",
                        new Dictionary<string, object?> { ["query"] = "x" })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(message) { ModelId = "stub-model" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
