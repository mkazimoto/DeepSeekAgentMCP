using System.Collections.Concurrent;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Gerencia múltiplas sessões de conversa, cada uma com seu próprio agente.
/// Cada sessão possui um DeepSeekAgent isolado com seu próprio histórico.
/// </summary>
public class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly DeepSeekClient _deepSeekClient;
    private readonly McpToolManager _mcpToolManager;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
    private bool _disposed;

    public SessionManager(DeepSeekClient deepSeekClient, McpToolManager mcpToolManager)
    {
        _deepSeekClient = deepSeekClient;
        _mcpToolManager = mcpToolManager;

        // Cleanup stale sessions every 15 minutes
        _cleanupTimer = new Timer(_ => CleanupStaleSessions(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    /// <summary>
    /// Processes a user message in the specified session and returns the agent's response.
    /// </summary>
    public async Task<string> ProcessMessageAsync(string sessionId, string userMessage, string? clientIp = null, CancellationToken cancellationToken = default)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);

        try
        {
            var state = GetOrCreateSession(sessionId, clientIp);

            // Create a CancellationTokenSource linked to the caller's token
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRequests[sessionId] = cts;

            try
            {
                state.LastAccess = DateTime.UtcNow;
                return await state.Agent.ProcessMessageAsync(userMessage, cts.Token);
            }
            finally
            {
                _activeRequests.TryRemove(sessionId, out _);
                cts.Dispose();
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Processes a user message with streaming output via callback.
    /// </summary>
    public async Task<string> ProcessMessageStreamingAsync(
        string sessionId, string userMessage,
        Action<string>? onChunk = null,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);

        try
        {
            var state = GetOrCreateSession(sessionId, clientIp);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRequests[sessionId] = cts;

            try
            {
                state.LastAccess = DateTime.UtcNow;

                // Subscribe to streaming output
                void Handler(string chunk) => onChunk?.Invoke(chunk);
                state.Agent.OnStreamingOutput += Handler;

                try
                {
                    return await state.Agent.ProcessMessageStreamingAsync(userMessage, cts.Token);
                }
                finally
                {
                    state.Agent.OnStreamingOutput -= Handler;
                }
            }
            finally
            {
                _activeRequests.TryRemove(sessionId, out _);
                cts.Dispose();
            }
        }
        finally
        {
            sessionLock.Release();
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
    /// Aguarda o semáforo da sessão para evitar race condition com ProcessMessageAsync.
    /// </summary>
    public async Task ClearConversationAsync(string sessionId)
    {
        SemaphoreSlim? sessionLock = null;
        if (_sessionLocks.TryGetValue(sessionId, out var found))
        {
            sessionLock = found;
            await sessionLock.WaitAsync();
        }

        try
        {
            if (_sessions.TryGetValue(sessionId, out var state))
            {
                state.Agent.ClearConversation();
                state.LastAccess = DateTime.UtcNow;
            }
        }
        finally
        {
            sessionLock?.Release();
        }
    }

    /// <summary>
    /// Returns the conversation history for a session (excluding system prompt).
    /// </summary>
    public IReadOnlyList<ChatMessage> GetHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            return state.Agent.GetConversationHistory();
        }
        return [];
    }

    /// <summary>
    /// Removes a session entirely and disposes its agent.
    /// </summary>
    public bool RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
            Console.WriteLine($"[Session] Removed session: {sessionId}");

            // Remove e dispose do semáforo da sessão
            if (_sessionLocks.TryRemove(sessionId, out var sessionLock))
            {
                sessionLock.Dispose();
            }

            // Fire-and-forget dispose do agente
            _ = DisposeAgentAsync(state.Agent);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets the count of active sessions.
    /// </summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>
    /// Gets the number of sessions associated with a given client IP.
    /// </summary>
    public int GetSessionCountForIp(string clientIp)
    {
        if (string.IsNullOrEmpty(clientIp)) return 0;
        return _sessions.Values.Count(s =>
            !string.IsNullOrEmpty(s.ClientIp) &&
            string.Equals(s.ClientIp, clientIp, StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks whether a session with the given ID already exists.
    /// </summary>
    public bool SessionExists(string sessionId)
    {
        return _sessions.ContainsKey(sessionId);
    }

    private SessionState GetOrCreateSession(string sessionId, string? clientIp = null)
    {
        return _sessions.GetOrAdd(sessionId, id =>
        {
            Console.WriteLine($"[Session] Created new session: {id}");
            return new SessionState
            {
                Agent = new DeepSeekAgent(_deepSeekClient, _mcpToolManager),
                CreatedAt = DateTime.UtcNow,
                LastAccess = DateTime.UtcNow,
                ClientIp = clientIp
            };
        });
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
            if (_sessions.TryRemove(id, out var state))
            {
                Console.WriteLine($"[Session] Cleaned up stale session: {id}");

                // Remove e dispose do semáforo da sessão
                if (_sessionLocks.TryRemove(id, out var sessionLock))
                {
                    sessionLock.Dispose();
                }

                // Dispose do agente (fire-and-forget dentro do timer)
                _ = DisposeAgentAsync(state.Agent);
            }
        }

        if (staleIds.Count > 0)
        {
            Console.WriteLine($"[Session] Cleanup complete: {staleIds.Count} session(s) removed. Active: {_sessions.Count}");
        }
    }

    /// <summary>
    /// Disposes all sessions asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();

        // Dispose all session semaphores
        foreach (var kvp in _sessionLocks)
        {
            if (_sessionLocks.TryRemove(kvp.Key, out var semaphore))
            {
                semaphore.Dispose();
            }
        }

        // Dispose all agents
        var agents = _sessions.Values.Select(s => s.Agent).ToList();
        _sessions.Clear();

        foreach (var agent in agents)
        {
            try
            {
                await agent.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Session] Error disposing agent: {ex.Message}");
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Helper to safely dispose an agent asynchronously.
    /// </summary>
    private static async Task DisposeAgentAsync(DeepSeekAgent agent)
    {
        try
        {
            await agent.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Session] Error disposing agent: {ex.Message}");
        }
    }

    private class SessionState
    {
        public DeepSeekAgent Agent { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccess { get; set; }
        public string? ClientIp { get; set; }
    }
}
