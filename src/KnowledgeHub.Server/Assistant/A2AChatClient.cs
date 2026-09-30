using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Assistant;

/// <summary>
/// <see cref="IChatClient"/> adapter over an A2A agent (SPEC-20260929-a2a-assistant-
/// delegation RF-003): each chat request becomes a <c>message/send</c> to the remote
/// agent; the reply text (Message parts or task artifacts/status) is returned as a
/// single assistant <see cref="ChatResponse"/>. Sub-task prompts are single-shot —
/// multi-turn threads map to one A2A message carrying the concatenated transcript.
/// </summary>
public sealed class A2AChatClient(A2AClient client, string skill) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = string.Join("\n\n", messages.Select(m =>
            m.Role == ChatRole.User ? m.Text : $"[{m.Role.Value}] {m.Text}"));
        var request = new SendMessageRequest
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [Part.FromText(text)],
                Metadata = new Dictionary<string, JsonElement>
                {
                    ["skill"] = JsonSerializer.SerializeToElement(skill)
                }
            }
        };
        var response = await client.SendMessageAsync(request, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, ExtractText(response)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Sub-tasks are non-streaming; surface the full reply as one update.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => client.Dispose();

    private static string ExtractText(SendMessageResponse response) =>
        response.PayloadCase switch
        {
            SendMessageResponseCase.Message => PartsText(response.Message?.Parts),
            SendMessageResponseCase.Task => TaskText(response.Task),
            _ => ""
        };

    private static string TaskText(AgentTask? task)
    {
        if (task is null)
            return "";
        var artifact = task.Artifacts?.LastOrDefault()?.Parts is { Count: > 0 } parts
            ? PartsText(parts)
            : null;
        return string.IsNullOrEmpty(artifact)
            ? PartsText(task.Status?.Message?.Parts)
            : artifact;
    }

    private static string PartsText(IEnumerable<Part>? parts) =>
        parts is null ? "" : string.Join("\n", parts.Where(p => p.Text is not null).Select(p => p.Text));
}
