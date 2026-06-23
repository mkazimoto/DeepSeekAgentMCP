using DeepSeekAgentMCP;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP.Tests;

public class DeepSeekAgentTests
{
    private static DeepSeekChatResponse MakeResponse(string? content, List<ToolCall>? toolCalls = null)
    {
        return new DeepSeekChatResponse
        {
            Id = "test-id",
            Object = "chat.completion",
            Created = 1234567890,
            Model = "deepseek-v4-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new ChatMessage { Role = "assistant", Content = toolCalls is { Count: > 0 } ? null : content, ToolCalls = toolCalls },
                    FinishReason = toolCalls is { Count: > 0 } ? "tool_calls" : "stop"
                }
            ],
            Usage = new Usage { PromptTokens = 5, CompletionTokens = 10, TotalTokens = 15 }
        };
    }

    private static ToolCall MakeToolCall(string name, string args = "{}")
    {
        return new ToolCall
        {
            Id = $"call_{Guid.NewGuid():N}",
            Type = "function",
            Function = new FunctionCall { Name = name, Arguments = args }
        };
    }

    [Fact]
    public void Constructor_WithValidParameters_Succeeds()
    {
        var agent = new DeepSeekAgent(new FakeDeepSeekClient(), new FakeMcpToolManager());
        Assert.NotNull(agent);
    }

    [Fact]
    public void GetConversationHistory_ReturnsEmptyInitially()
    {
        var agent = new DeepSeekAgent(new FakeDeepSeekClient(), new FakeMcpToolManager());
        Assert.Empty(agent.GetConversationHistory());
    }

    [Fact]
    public void ClearConversation_Works()
    {
        var agent = new DeepSeekAgent(new FakeDeepSeekClient(), new FakeMcpToolManager());
        agent.ClearConversation();
        Assert.Empty(agent.GetConversationHistory());
    }

    [Fact]
    public async Task ProcessMessageAsync_WhenMcpOffline_ReturnsOfflineMessage()
    {
        var client = new FakeDeepSeekClient();
        client.EnqueueResponse(MakeResponse("Should not be called"));
        var mgr = new FakeMcpToolManager { SimulateConnected = false };
        var agent = new DeepSeekAgent(client, mgr);

        var result = await agent.ProcessMessageAsync("Hi");
        Assert.Contains("não está conectado", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessMessageStreamingAsync_WhenMcpOffline_ReturnsOfflineMessage()
    {
        var client = new FakeDeepSeekClient();
        client.EnqueueResponse(MakeResponse("Should not be called"));
        var mgr = new FakeMcpToolManager { SimulateConnected = false };
        var agent = new DeepSeekAgent(client, mgr, streamingEnabled: false);

        var result = await agent.ProcessMessageStreamingAsync("Hi");
        Assert.Contains("não está conectado", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessMessageAsync_NoToolCalls_ReturnsResponse()
    {
        var client = new FakeDeepSeekClient();
        client.EnqueueResponse(MakeResponse("Hello!"));
        var agent = new DeepSeekAgent(client, new FakeMcpToolManager());

        var result = await agent.ProcessMessageAsync("Hi");
        Assert.Equal("Hello!", result);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithToolCall_ExecutesTool()
    {
        var toolCall = MakeToolCall("srv_test", "{}");
        var client = new FakeDeepSeekClient();
        client.EnqueueResponse(MakeResponse(null, [toolCall]));
        client.EnqueueResponse(MakeResponse("Done!"));

        var mgr = new FakeMcpToolManager();
        mgr.SetToolDefinitions([new ToolDefinition { Function = new ToolFunction { Name = "srv_test" } }]);
        mgr.SetToolResult("srv_test", "ok");

        var agent = new DeepSeekAgent(client, mgr);
        var result = await agent.ProcessMessageAsync("Do it");

        Assert.Equal("Done!", result);
        Assert.Single(mgr.ExecutedToolCalls);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithToolErrorAndContinueOnError_Continues()
    {
        var toolCall = MakeToolCall("srv_tool", "{}");
        var client = new FakeDeepSeekClient();
        client.EnqueueResponse(MakeResponse(null, [toolCall]));
        client.EnqueueResponse(MakeResponse("Continues"));

        var mgr = new FakeMcpToolManager();
        mgr.SetToolDefinitions([new ToolDefinition { Function = new ToolFunction { Name = "srv_tool" } }]);
        mgr.SetToolResult("srv_tool", "Error: failed");

        var agent = new DeepSeekAgent(client, mgr, continueOnToolError: true);
        var result = await agent.ProcessMessageAsync("Run");
        Assert.Equal("Continues", result);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithToolErrorAndStopOnError_ReturnsError()
    {
        var toolCall = MakeToolCall("srv_tool", "{}");
        var client = new FakeDeepSeekClient();
        client.EnqueueResponse(MakeResponse(null, [toolCall]));

        var mgr = new FakeMcpToolManager();
        mgr.SetToolDefinitions([new ToolDefinition { Function = new ToolFunction { Name = "srv_tool" } }]);
        mgr.SetToolResult("srv_tool", "Error: failed");

        var agent = new DeepSeekAgent(client, mgr, continueOnToolError: false);
        var result = await agent.ProcessMessageAsync("Run");
        Assert.Contains("erro", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessMessageAsync_Cancellation_ThrowsWhenPreCancelled()
    {
        var agent = new DeepSeekAgent(new FakeDeepSeekClient(), new FakeMcpToolManager());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            agent.ProcessMessageAsync("Hello", cts.Token));
    }

    [Fact]
    public async Task ProcessMessageAsync_HistoryTruncated_WhenExceedsLimit()
    {
        var client = new FakeDeepSeekClient();
        for (int i = 0; i < 5; i++) client.EnqueueResponse(MakeResponse("OK"));

        var agent = new DeepSeekAgent(client, new FakeMcpToolManager(), maxHistoryMessages: 3);
        await agent.ProcessMessageAsync("M1");
        await agent.ProcessMessageAsync("M2");
        await agent.ProcessMessageAsync("M3");
        await agent.ProcessMessageAsync("M4");
        Assert.True(agent.GetConversationHistory().Count >= 3);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var agent = new DeepSeekAgent(new FakeDeepSeekClient(), new FakeMcpToolManager());
        await agent.DisposeAsync();
    }
}
