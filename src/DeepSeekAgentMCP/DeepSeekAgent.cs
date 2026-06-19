using System.Collections.Concurrent;
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
    private readonly bool _parallelToolCalls;
    private readonly bool _summarizeHistory;
    private readonly int _toolCacheTtlSeconds;
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
        bool continueOnToolError = true,
        bool parallelToolCalls = true,
        bool summarizeHistory = false,
        int toolCacheTtlSeconds = 30)
    {
        _deepSeek = deepSeek;
        _mcpToolManager = mcpToolManager;
        _maxHistoryMessages = maxHistoryMessages;
        _streamingEnabled = streamingEnabled;
        _continueOnToolError = continueOnToolError;
        _parallelToolCalls = parallelToolCalls;
        _summarizeHistory = summarizeHistory;
        _toolCacheTtlSeconds = toolCacheTtlSeconds;

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

        // Optionally summarize old history to preserve context
        if (_summarizeHistory)
        {
            await SummarizeOldHistoryAsync(cancellationToken);
        }

        // Get tool definitions from MCP (with cache)
        var tools = _mcpToolManager.GetToolDefinitions(_toolCacheTtlSeconds);

        var maxIterations = 30;
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
                var (toolResults, toolFailed) = await ExecuteToolCallsAsync(message.ToolCalls, cancellationToken);

                // Add tool results to history in original order
                lock (_historyLock)
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        if (toolResults.TryGetValue(toolCall.Id, out var pair))
                        {
                            _conversationHistory.Add(new ChatMessage
                            {
                                Role = "tool",
                                ToolCallId = pair.Call.Id,
                                Name = pair.Call.Function.Name,
                                Content = pair.Result
                            });
                        }
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
                    return "Ocorreu um erro ao executar uma ferramenta. A solicitação não pôde ser concluída.";
                }

                continue;
            }

            // No tool calls - this is the final response
            return message.Content ?? string.Empty;
        }

        return "O agente atingiu o número máximo de iterações. Por favor, tente uma solicitação mais simples.";
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

        // Optionally summarize old history to preserve context
        if (_summarizeHistory)
        {
            await SummarizeOldHistoryAsync(cancellationToken);
        }

        var tools = _mcpToolManager.GetToolDefinitions(_toolCacheTtlSeconds);

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
            var (toolResults, _) = await ExecuteToolCallsAsync(message.ToolCalls, cancellationToken);

            // Add tool results to history in original order
            lock (_historyLock)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    if (toolResults.TryGetValue(toolCall.Id, out var pair))
                    {
                        _conversationHistory.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = pair.Call.Id,
                            Name = pair.Call.Function.Name,
                            Content = pair.Result
                        });
                    }
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

        return message.Content ?? string.Empty;
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
        if (_conversationHistory.Count <= _maxHistoryMessages) return;

        // Keep the system prompt (index 0), remove oldest messages
        _conversationHistory.RemoveRange(1, _conversationHistory.Count - _maxHistoryMessages);
    }

    /// <summary>
    /// Summarizes old conversation history via DeepSeek to preserve context without growing token count.
    /// Only activates when SummarizeHistory is enabled and there are enough messages to summarize.
    /// </summary>
    private async Task SummarizeOldHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!_summarizeHistory) return;

        List<ChatMessage> messagesToSummarize;
        lock (_historyLock)
        {
            if (_conversationHistory.Count <= _maxHistoryMessages + 5) return;

            // Keep the last _maxHistoryMessages/2 messages intact, summarize the rest
            var keepCount = Math.Max(_maxHistoryMessages / 2, 10);
            var summarizeCount = _conversationHistory.Count - keepCount - 1; // -1 for system prompt
            if (summarizeCount < 5) return;

            messagesToSummarize = _conversationHistory.Skip(1).Take(summarizeCount).ToList();
        }

        // Build a prompt for summarization
        var summaryPrompt = new StringBuilder();
        summaryPrompt.AppendLine("Summarize the following conversation history concisely, preserving key information, decisions, and context needed for future responses:");
        summaryPrompt.AppendLine();

        foreach (var msg in messagesToSummarize)
        {
            var role = msg.Role switch
            {
                "user" => "User",
                "assistant" => "Assistant",
                "tool" => $"Tool ({msg.Name})",
                _ => msg.Role
            };
            var content = msg.Content?.Length > 500 ? msg.Content[..500] + "..." : msg.Content;
            summaryPrompt.AppendLine($"{role}: {content}");
        }

        try
        {
            var summarizeMessages = new List<ChatMessage>
            {
                new() { Role = "user", Content = summaryPrompt.ToString() }
            };

            var summaryResponse = await _deepSeek.SendChatAsync(summarizeMessages, cancellationToken: cancellationToken);
            var summary = summaryResponse.Choices?.FirstOrDefault()?.Message?.Content;

            if (!string.IsNullOrWhiteSpace(summary))
            {
                lock (_historyLock)
                {
                    // Remove the summarized messages and insert a system message with the summary
                    var systemMsg = _conversationHistory[0];
                    _conversationHistory.RemoveRange(1, messagesToSummarize.Count);
                    _conversationHistory.Insert(1, new ChatMessage
                    {
                        Role = "system",
                        Content = $"[Conversation Summary]\n{summary}"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Agent] History summarization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a list of tool calls (parallel or sequential) with error handling.
    /// Shared between ProcessMessageAsync and ProcessMessageStreamingAsync
    /// to eliminate ~70% code duplication.
    /// </summary>
    private async Task<(ConcurrentDictionary<string, (ToolCall Call, string Result)> Results, bool Failed)> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls, CancellationToken cancellationToken)
    {
        var toolResults = new ConcurrentDictionary<string, (ToolCall Call, string Result)>();
        var toolFailed = false;

        if (_parallelToolCalls && toolCalls.Count > 1)
        {
            using var parallelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = parallelCts.Token,
                MaxDegreeOfParallelism = Math.Min(toolCalls.Count, 3)
            };

            await Parallel.ForEachAsync(toolCalls, parallelOptions, async (toolCall, ct) =>
            {
                ct.ThrowIfCancellationRequested();

                Console.WriteLine($"\n[Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                string result;
                try
                {
                    result = await _mcpToolManager.ExecuteToolCallAsync(toolCall, ct);
                }
                catch (Exception ex)
                {
                    result = $"Error: {ex.Message}";
                }

                var isError = result.StartsWith("Error:", StringComparison.Ordinal);
                if (isError)
                {
                    Console.WriteLine($"[Tool Error] {result}");
                    if (!_continueOnToolError)
                    {
                        toolFailed = true;
                        parallelCts.Cancel();
                    }
                }
                else
                {
                    Console.WriteLine($"[Tool Result] {result[..Math.Min(result.Length, 200)]}{(result.Length > 200 ? "..." : "")}");
                }

                toolResults[toolCall.Id] = (toolCall, result);
            });

            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            foreach (var toolCall in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Console.WriteLine($"\n[Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                string toolResult;
                try
                {
                    toolResult = await _mcpToolManager.ExecuteToolCallAsync(toolCall, cancellationToken);
                }
                catch (Exception ex)
                {
                    toolResult = $"Error: {ex.Message}";
                }

                var isError = toolResult.StartsWith("Error:", StringComparison.Ordinal);
                if (isError)
                {
                    Console.WriteLine($"[Tool Error] {toolResult}");
                }
                else
                {
                    Console.WriteLine($"[Tool Result] {toolResult[..Math.Min(toolResult.Length, 200)]}{(toolResult.Length > 200 ? "..." : "")}");
                }

                toolResults[toolCall.Id] = (toolCall, toolResult);

                if (isError && !_continueOnToolError)
                {
                    toolFailed = true;
                    break;
                }
            }
        }

        return (toolResults, toolFailed);
    }

    public ValueTask DisposeAsync()
    {
        // DeepSeekClient e McpToolManager são owned pelo SessionManager e compartilhados
        // entre todas as sessões. Não os dispor aqui — o SessionManager cuida disso.
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
