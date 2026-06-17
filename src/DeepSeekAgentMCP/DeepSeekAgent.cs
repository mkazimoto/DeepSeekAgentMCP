using System.Text;
using System.Text.Json;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Main agent that orchestrates the conversation between the user, DeepSeek, and MCP tools.
/// </summary>
public class DeepSeekAgent : IAsyncDisposable
{
    private readonly DeepSeekClient _deepSeek;
    private readonly McpToolManager _mcpToolManager;
    private readonly List<ChatMessage> _conversationHistory = [];
    private readonly int _maxHistoryMessages;
    private readonly bool _streamingEnabled;

    private static readonly string SystemPrompt = BuildSystemPrompt();

    private static string BuildSystemPrompt()
    {
        var basePrompt = SkillLoader.LoadInstructions();
        var skillsContent = SkillLoader.LoadSkillsToPrompt();
        return basePrompt + skillsContent;
    }

    /// <summary>
    /// Event raised when the agent produces streaming output.
    /// </summary>
    public event Action<string>? OnStreamingOutput;

    public DeepSeekAgent(
        DeepSeekClient deepSeek,
        McpToolManager mcpToolManager,
        int maxHistoryMessages = 50,
        bool streamingEnabled = true)
    {
        _deepSeek = deepSeek;
        _mcpToolManager = mcpToolManager;
        _maxHistoryMessages = maxHistoryMessages;
        _streamingEnabled = streamingEnabled;

        _conversationHistory.Add(new ChatMessage
        {
            Role = "system",
            Content = SystemPrompt
        });
    }

    /// <summary>
    /// Initializes the agent by connecting to MCP servers.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n=== Initializing MCP Servers ===");
        await _mcpToolManager.InitializeAsync(cancellationToken);
        Console.WriteLine("=== Initialization Complete ===\n");
    }

    /// <summary>
    /// Processes a user message and returns the agent's response.
    /// </summary>
    public async Task<string> ProcessMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        // Add user message to history
        _conversationHistory.Add(new ChatMessage
        {
            Role = "user",
            Content = userMessage
        });

        // Truncate history if needed (always keep system prompt)
        TrimConversationHistory();

        // Get tool definitions from MCP
        var tools = _mcpToolManager.GetToolDefinitions();

        var maxIterations = 10;
        var currentIteration = 0;

        while (currentIteration < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentIteration++;

            // Send to DeepSeek
            DeepSeekChatResponse response;
            try
            {
                response = await _deepSeek.SendChatAsync(
                    _conversationHistory,
                    tools: tools.Count > 0 ? tools : null,
                    toolChoice: null,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var cancelMsg = "O pedido foi cancelado pelo usuário.";
                _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = cancelMsg });
                return cancelMsg;
            }
            catch (HttpRequestException ex)
            {
                var error = $"Error communicating with DeepSeek API: {ex.Message}";
                _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = error });
                return error;
            }

            var choice = response.Choices.FirstOrDefault();
            if (choice == null)
            {
                var error = "No response from DeepSeek.";
                _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = error });
                return error;
            }

            var message = choice.Message;

            // Add assistant message to history
            _conversationHistory.Add(message);

            // Check if the model wants to call tools
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Console.WriteLine($"\n[Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                    var toolResult = await _mcpToolManager.ExecuteToolCallAsync(toolCall, cancellationToken);

                    Console.WriteLine($"[Tool Result] {toolResult[..Math.Min(toolResult.Length, 200)]}{(toolResult.Length > 200 ? "..." : "")}");

                    // Add tool result to conversation
                    _conversationHistory.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Function.Name,
                        Content = toolResult
                    });
                }

                // Trim history after adding tool results
                TrimConversationHistory();
                continue;
            }

            // No tool calls - this is the final response
            var finalContent = message.Content;
            return finalContent;
        }

        return "The agent reached the maximum number of iterations. Please try a simpler request.";
    }

    /// <summary>
    /// Processes a user message with streaming output.
    /// </summary>
    public async Task<string> ProcessMessageStreamingAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _conversationHistory.Add(new ChatMessage
        {
            Role = "user",
            Content = userMessage
        });

        TrimConversationHistory();
        var tools = _mcpToolManager.GetToolDefinitions();

        // First, let's check if the model needs to make tool calls.
        // We use a non-streaming call to decide, then stream the final response.
        var response = await _deepSeek.SendChatAsync(
            _conversationHistory,
            tools: tools.Count > 0 ? tools : null,
            cancellationToken: cancellationToken);

        var choice = response.Choices.FirstOrDefault();
        if (choice == null) return "No response from DeepSeek.";

        var message = choice.Message;
        _conversationHistory.Add(message);

        // Handle tool calls if any
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in message.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Console.WriteLine($"\n[Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                var toolResult = await _mcpToolManager.ExecuteToolCallAsync(toolCall, cancellationToken);
                Console.WriteLine($"[Tool Result] {toolResult[..Math.Min(toolResult.Length, 200)]}...");

                _conversationHistory.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Function.Name,
                    Content = toolResult
                });
            }

            TrimConversationHistory();

            // After tool calls, stream the final response
            var fullContent = new StringBuilder();
            await foreach (var chunk in _deepSeek.SendChatStreamingAsync(_conversationHistory, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fullContent.Append(chunk);
                OnStreamingOutput?.Invoke(chunk);
            }

            var result = fullContent.ToString();
            _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = result });
            return result;
        }

        // No tool calls - this is the response
        if (_streamingEnabled)
        {
            var fullContent = new StringBuilder();
            await foreach (var chunk in _deepSeek.SendChatStreamingAsync(
                _conversationHistory, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fullContent.Append(chunk);
                OnStreamingOutput?.Invoke(chunk);
            }

            // Replace the non-streaming message with streamed content
            _conversationHistory.Remove(message);
            var streamedContent = fullContent.ToString();
            _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = streamedContent });
            return streamedContent;
        }

        return message.Content;
    }

    /// <summary>
    /// Returns the MCP status summary.
    /// </summary>
    public string GetMcpStatus() => _mcpToolManager.GetStatusSummary();

    /// <summary>
    /// Clears the conversation history (keeps the system prompt).
    /// </summary>
    public void ClearConversation()
    {
        var systemMessage = _conversationHistory[0];
        _conversationHistory.Clear();
        _conversationHistory.Add(systemMessage);
    }

    /// <summary>
    /// Returns the conversation history (excluding system prompt).
    /// </summary>
    public IReadOnlyList<ChatMessage> GetConversationHistory() =>
        _conversationHistory.Skip(1).ToList().AsReadOnly();

    private void TrimConversationHistory()
    {
        if (_conversationHistory.Count <= _maxHistoryMessages) return;

        // Keep the system prompt, remove oldest messages
        var systemMessage = _conversationHistory[0];
        _conversationHistory.RemoveRange(1, _conversationHistory.Count - _maxHistoryMessages);
        _conversationHistory[0] = systemMessage;
    }

    public async ValueTask DisposeAsync()
    {
        await _mcpToolManager.DisposeAsync();
        _deepSeek.Dispose();
    }
}
