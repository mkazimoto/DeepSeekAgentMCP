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
    private readonly bool _continueOnToolError;
    private readonly object _historyLock = new();
    private readonly object _eventLock = new();

    private static readonly string SystemPrompt = BuildSystemPrompt();

    private static string BuildSystemPrompt()
    {
        var basePrompt = SkillLoader.LoadInstructions();
        var skillsContent = SkillLoader.LoadSkillsToPrompt();
        return basePrompt + skillsContent;
    }

    private Action<string>? _streamingHandler;

    /// <summary>
    /// Event raised when the agent produces streaming output.
    /// </summary>
    public event Action<string>? OnStreamingOutput
    {
        add { lock (_eventLock) _streamingHandler += value; }
        remove { lock (_eventLock) _streamingHandler -= value; }
    }

    public DeepSeekAgent(
        DeepSeekClient deepSeek,
        McpToolManager mcpToolManager,
        int maxHistoryMessages = 50,
        bool streamingEnabled = true,
        bool continueOnToolError = true)
    {
        _deepSeek = deepSeek;
        _mcpToolManager = mcpToolManager;
        _maxHistoryMessages = maxHistoryMessages;
        _streamingEnabled = streamingEnabled;
        _continueOnToolError = continueOnToolError;

        lock (_historyLock)
        {
            _conversationHistory.Add(new ChatMessage
            {
                Role = "system",
                Content = SystemPrompt
            });
        }
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
        lock (_historyLock)
        {
            _conversationHistory.Add(new ChatMessage
            {
                Role = "user",
                Content = userMessage
            });

            // Truncate history if needed (always keep system prompt)
            TrimConversationHistory();
        }

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
                lock (_historyLock)
                {
                    _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = cancelMsg });
                }
                return cancelMsg;
            }
            catch (HttpRequestException ex)
            {
                var error = $"Error communicating with DeepSeek API: {ex.Message}";
                lock (_historyLock)
                {
                    _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = error });
                }
                return error;
            }

            var choice = response.Choices.FirstOrDefault();
            if (choice == null)
            {
                var error = "No response from DeepSeek.";
                lock (_historyLock)
                {
                    _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = error });
                }
                return error;
            }

            var message = choice.Message;

            // Add assistant message to history
            lock (_historyLock)
            {
                _conversationHistory.Add(message);
            }

            // Check if the model wants to call tools
            if (message.ToolCalls is { Count: > 0 })
            {
                var toolFailed = false;
                foreach (var toolCall in message.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Console.WriteLine($"\n[Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                    var toolResult = await _mcpToolManager.ExecuteToolCallAsync(toolCall, cancellationToken);

                    var isError = toolResult.StartsWith("Error:", StringComparison.Ordinal);
                    if (isError)
                    {
                        Console.WriteLine($"[Tool Error] {toolResult}");
                    }
                    else
                    {
                        Console.WriteLine($"[Tool Result] {toolResult[..Math.Min(toolResult.Length, 200)]}{(toolResult.Length > 200 ? "..." : "")}");
                    }

                    // If tool failed and ContinueOnToolError is false, stop the batch
                    if (isError && !_continueOnToolError)
                    {
                        toolFailed = true;
                        lock (_historyLock)
                        {
                            _conversationHistory.Add(new ChatMessage
                            {
                                Role = "tool",
                                ToolCallId = toolCall.Id,
                                Name = toolCall.Function.Name,
                                Content = toolResult
                            });
                        }
                        break;
                    }

                    // Add tool result to conversation
                    lock (_historyLock)
                    {
                        _conversationHistory.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            Name = toolCall.Function.Name,
                            Content = toolResult
                        });
                    }
                }

                // Trim history after adding tool results
                lock (_historyLock)
                {
                    TrimConversationHistory();
                }

                // If a tool failed and ContinueOnToolError is false, return error to user
                if (toolFailed)
                {
                    return "An error occurred while executing a tool. The request could not be completed.";
                }

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

        lock (_historyLock)
        {
            _conversationHistory.Add(new ChatMessage
            {
                Role = "user",
                Content = userMessage
            });

            TrimConversationHistory();
        }
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
        lock (_historyLock)
        {
            _conversationHistory.Add(message);
        }

        // Handle tool calls if any
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in message.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Console.WriteLine($"\n[Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                var toolResult = await _mcpToolManager.ExecuteToolCallAsync(toolCall, cancellationToken);
                Console.WriteLine($"[Tool Result] {toolResult[..Math.Min(toolResult.Length, 200)]}...");

                lock (_historyLock)
                {
                    _conversationHistory.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Function.Name,
                        Content = toolResult
                    });
                }
            }

            lock (_historyLock)
            {
                TrimConversationHistory();
            }

            // After tool calls, stream the final response
            var fullContent = new StringBuilder();
            await foreach (var chunk in _deepSeek.SendChatStreamingAsync(_conversationHistory, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fullContent.Append(chunk);
                Action<string>? handler;
                lock (_eventLock) handler = _streamingHandler;
                handler?.Invoke(chunk);
            }

            var result = fullContent.ToString();
            lock (_historyLock)
            {
                _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = result });
            }
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
                Action<string>? handler;
                lock (_eventLock) handler = _streamingHandler;
                handler?.Invoke(chunk);
            }

            // Replace the non-streaming message with streamed content
            var streamedContent = fullContent.ToString();
            lock (_historyLock)
            {
                _conversationHistory.Remove(message);
                _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = streamedContent });
            }
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
        lock (_historyLock)
        {
            var systemMessage = _conversationHistory[0];
            _conversationHistory.Clear();
            _conversationHistory.Add(systemMessage);
        }
    }

    /// <summary>
    /// Returns the conversation history (excluding system prompt).
    /// </summary>
    public IReadOnlyList<ChatMessage> GetConversationHistory()
    {
        lock (_historyLock)
        {
            return _conversationHistory.Skip(1).ToList().AsReadOnly();
        }
    }

    private void TrimConversationHistory()
    {
        // NOTE: Este método é SEMPRE chamado dentro de lock(_historyLock)
        // O lock é reentrante (Monitor), então é seguro ser chamado dentro
        // de um bloco lock(_historyLock) já existente.
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
