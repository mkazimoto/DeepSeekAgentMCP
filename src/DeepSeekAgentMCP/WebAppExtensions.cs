using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Extension methods for WebApplication to register common agent API endpoints.
/// Elimina duplicação de rotas entre Program.cs e DeepSeekAgentService.cs.
/// </summary>
public static class WebAppExtensions
{
    /// <summary>
    /// Maps all common agent API endpoints (chat, status, history, cancel, clear, health).
    /// </summary>
    public static WebApplication MapAgentEndpoints(
        this WebApplication app,
        SessionManager sessionManager,
        McpToolManager mcpManager,
        string model,
        ILogger? logger = null)
    {
        // POST /api/chat — send a message
        app.MapPost("/api/chat", async (ChatRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message is required." });

            try
            {
                var sessionId = GetSessionId(request);
                var response = await sessionManager.ProcessMessageAsync(sessionId, request.Message, ct);
                return Results.Ok(new { response });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error processing chat message");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/status — MCP server status + connected servers
        app.MapGet("/api/status", () =>
        {
            return Results.Ok(new
            {
                model,
                activeSessions = sessionManager.ActiveSessionCount,
                mcpServers = mcpManager.GetServerStatusList()
            });
        });

        // GET /api/history — conversation history for a session
        app.MapGet("/api/history", (string? sessionId) =>
        {
            var sid = sessionId ?? "default";
            var history = sessionManager.GetHistory(sid);
            var messages = history.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                name = m.Name
            });
            return Results.Ok(new { history = messages });
        });

        // POST /api/cancel — cancel an active request for a session
        app.MapPost("/api/cancel", (ChatRequest request) =>
        {
            var sessionId = GetSessionId(request);
            sessionManager.CancelRequest(sessionId);
            return Results.Ok(new { success = true });
        });

        // POST /api/clear — clear conversation for a session
        app.MapPost("/api/clear", (ChatRequest request) =>
        {
            var sessionId = GetSessionId(request);
            sessionManager.ClearConversation(sessionId);
            return Results.Ok(new { success = true });
        });

        // GET /api/health — liveness/readiness probe
        app.MapGet("/api/health", () =>
        {
            return Results.Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                activeSessions = sessionManager.ActiveSessionCount
            });
        });


        app.MapFallbackToFile("index.html");

        return app;
    }

    internal static string GetSessionId(ChatRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : "default";
    }
}
