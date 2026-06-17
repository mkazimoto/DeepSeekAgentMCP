using DeepSeekAgentMCP;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP.Tests;

/// <summary>
/// Fake DeepSeekClient for testing — returns pre-configured responses in queue order.
/// </summary>
public class FakeDeepSeekClient : DeepSeekClient
{
    private readonly Queue<DeepSeekChatResponse> _responses = new();
    public List<List<ChatMessage>> ReceivedMessages { get; } = [];
    public List<List<ToolDefinition>?> ReceivedTools { get; } = [];

    public FakeDeepSeekClient() : base("test-api-key") { }

    public void EnqueueResponse(DeepSeekChatResponse response)
    {
        _responses.Enqueue(response);
    }

    public override async Task<DeepSeekChatResponse> SendChatAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        string? toolChoice = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceivedMessages.Add(new List<ChatMessage>(messages));
        ReceivedTools.Add(tools);

        return _responses.TryDequeue(out var response)
            ? response
            : new DeepSeekChatResponse
            {
                Id = "fake-id",
                Object = "chat.completion",
                Created = 1234567890,
                Model = "test-model",
                Choices = [new Choice { Index = 0, Message = new ChatMessage { Role = "assistant", Content = "Default fake response" }, FinishReason = "stop" }],
                Usage = new Usage { PromptTokens = 5, CompletionTokens = 5, TotalTokens = 10 }
            };
    }
}
