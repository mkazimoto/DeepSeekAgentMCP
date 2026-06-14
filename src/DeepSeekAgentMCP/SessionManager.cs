using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Gerencia múltiplas sessões de conversa, cada uma com seu próprio histórico.
/// Resolve o problema de sessões compartilharem o mesmo contexto.
/// </summary>
public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);
    private readonly DeepSeekClient _deepSeekClient;
    private readonly McpToolManager _mcpToolManager;
    private readonly int _maxHistoryMessages;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);

    private const string SystemPrompt = """
        You are an intelligent agent with access to MCP (Model Context Protocol) tools.
        You can use these tools to perform various tasks like reading files, fetching web pages,
        managing code, and more. Always analyze the user's request and use the appropriate tools
        when needed. If you don't need tools, just respond directly.

        When calling tools, use the exact tool name as provided. Pass the correct arguments
        based on the tool's schema.

        You can call multiple tools in sequence if needed to fulfill a complex request.
        After you receive tool results, synthesize them into a helpful response for the user.
        """;

    public SessionManager(DeepSeekClient deepSeekClient, McpToolManager mcpToolManager, int maxHistoryMessages = 50)
    {
        _deepSeekClient = deepSeekClient;
        _mcpToolManager = mcpToolManager;
        _maxHistoryMessages = maxHistoryMessages;

        // Cleanup stale sessions every 15 minutes
        _cleanupTimer = new Timer(_ => CleanupStaleSessions(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    /// <summary>
    /// Processes a user message in the specified session and returns the agent's response.
    /// </summary>
    public async Task<string> ProcessMessageAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateSession(sessionId);

        // Create a CancellationTokenSource linked to the caller's token
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = cts.Token;

        // Store the CTS so it can be cancelled via CancelRequest
        _activeRequests[sessionId] = cts;

        try
        {
            // Add user message to session history
            state.History.Add(new ChatMessage
            {
                Role = "user",
                Content = userMessage
            });

            state.LastAccess = DateTime.UtcNow;
            TrimConversationHistory(state);

            var tools = _mcpToolManager.GetToolDefinitions();

            var maxIterations = 10;
            var currentIteration = 0;

            while (currentIteration < maxIterations)
            {
                currentIteration++;

                DeepSeekChatResponse response;
                try
                {
                    response = await _deepSeekClient.SendChatAsync(
                        state.History,
                        tools: tools.Count > 0 ? tools : null,
                        toolChoice: null,
                        cancellationToken: linkedToken);
                }
                catch (OperationCanceledException)
                {
                    var cancelMsg = "O pedido foi cancelado pelo usuário.";
                    state.History.Add(new ChatMessage { Role = "assistant", Content = cancelMsg });
                    return cancelMsg;
                }
                catch (HttpRequestException ex)
                {
                    var error = $"Error communicating with DeepSeek API: {ex.Message}";
                    state.History.Add(new ChatMessage { Role = "assistant", Content = error });
                    return error;
                }

                var choice = response.Choices.FirstOrDefault();
                if (choice == null)
                {
                    var error = "No response from DeepSeek.";
                    state.History.Add(new ChatMessage { Role = "assistant", Content = error });
                    return error;
                }

                var message = choice.Message;
                state.History.Add(message);

                // Check if the model wants to call tools
                if (message.ToolCalls is { Count: > 0 })
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        linkedToken.ThrowIfCancellationRequested();

                        Console.WriteLine($"[Session:{sessionId}] [Tool Call] {toolCall.Function.Name}({toolCall.Function.Arguments})");

                        var toolResult = await _mcpToolManager.ExecuteToolCallAsync(toolCall, linkedToken);

                        Console.WriteLine($"[Session:{sessionId}] [Tool Result] {toolResult[..Math.Min(toolResult.Length, 200)]}{(toolResult.Length > 200 ? "..." : "")}");

                        state.History.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            Name = toolCall.Function.Name,
                            Content = toolResult
                        });
                    }

                    TrimConversationHistory(state);
                    continue;
                }

                // No tool calls - this is the final response
                return message.Content;
            }

            return "The agent reached the maximum number of iterations. Please try a simpler request.";
        }
        finally
        {
            // Clean up the CTS
            _activeRequests.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Cancels an active request for the specified session.
    /// </summary>
    public void CancelRequest(string sessionId)
    {
        if (_activeRequests.TryRemove(sessionId, out var cts))
        {
            Console.WriteLine($"[Session] Cancelling request for session: {sessionId}");
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Clears the conversation history for a session (keeps system prompt).
    /// </summary>
    public void ClearConversation(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            state.History.Clear();
            state.History.Add(new ChatMessage
            {
                Role = "system",
                Content = SystemPrompt
            });
            state.LastAccess = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Returns the conversation history for a session (excluding system prompt).
    /// </summary>
    public IReadOnlyList<ChatMessage> GetHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            return state.History.Skip(1).ToList().AsReadOnly();
        }
        return [];
    }

    /// <summary>
    /// Removes a session entirely.
    /// </summary>
    public bool RemoveSession(string sessionId)
    {
        return _sessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Gets the count of active sessions.
    /// </summary>
    public int ActiveSessionCount => _sessions.Count;

    private SessionState GetOrCreateSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, id =>
        {
            Console.WriteLine($"[Session] Created new session: {id}");
            return new SessionState
            {
                History = new List<ChatMessage>
                {
                    new() { Role = "system", Content = SystemPrompt }
                },
                CreatedAt = DateTime.UtcNow,
                LastAccess = DateTime.UtcNow
            };
        });
    }

    private void TrimConversationHistory(SessionState state)
    {
        if (state.History.Count <= _maxHistoryMessages) return;

        var systemMessage = state.History[0];
        state.History.RemoveRange(1, state.History.Count - _maxHistoryMessages);
        state.History[0] = systemMessage;
    }

    private void CleanupStaleSessions()
    {
        var cutoff = DateTime.UtcNow - _sessionTimeout;
        var staleIds = _sessions
            .Where(kvp => kvp.Value.LastAccess < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            if (_sessions.TryRemove(id, out _))
            {
                Console.WriteLine($"[Session] Cleaned up stale session: {id}");
            }
        }

        if (staleIds.Count > 0)
        {
            Console.WriteLine($"[Session] Cleanup complete: {staleIds.Count} session(s) removed. Active: {_sessions.Count}");
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _sessions.Clear();
    }

    private class SessionState
    {
        public List<ChatMessage> History { get; set; } = [];
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccess { get; set; }
    }
}
