using System.Net;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Extension methods for WebApplication to register common agent API endpoints.
/// Elimina duplicação de rotas entre Program.cs e DeepSeekAgentService.cs.
/// </summary>
public static class WebAppExtensions
{
    // Rate limiter: 30 requests por minuto por IP (ajustável)
    private static readonly RateLimiter ChatRateLimiter = new(
        maxRequests: 30,
        windowSize: TimeSpan.FromMinutes(1));

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
        // POST /api/chat — send a message (com rate limiting e sanitização)
        app.MapPost("/api/chat", async (HttpContext httpContext, ChatRequest request, CancellationToken ct) =>
        {
            // --- Validações básicas ---
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message is required." });

            if (request.Message.Length > 10000)
                return Results.BadRequest(new { error = "Message exceeds maximum length of 10000 characters." });

            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId.Length > 100)
                return Results.BadRequest(new { error = "SessionId exceeds maximum length of 100 characters." });

            // --- Rate limiting por IP ---
            var clientIp = GetClientIp(httpContext);
            if (!ChatRateLimiter.TryConsume(clientIp))
            {
                logger?.LogWarning("Rate limit exceeded for IP: {ClientIp}", clientIp);
                var retryAfter = TimeSpan.FromMinutes(1);
                httpContext.Response.Headers.RetryAfter = retryAfter.TotalSeconds.ToString();
                return Results.Json(
                    new { error = "Too many requests. Please wait before sending another message.", retryAfterSeconds = (int)retryAfter.TotalSeconds },
                    statusCode: (int)HttpStatusCode.TooManyRequests);
            }

            // --- Sanitização da mensagem ---
            var sanitizedMessage = InputSanitizer.SanitizeMessage(request.Message);

            // Se após sanitizar ficou vazio, rejeitar
            if (string.IsNullOrWhiteSpace(sanitizedMessage))
                return Results.BadRequest(new { error = "Message contains no valid content after sanitization." });

            try
            {
                var sessionId = GetSessionId(request);
                var response = await sessionManager.ProcessMessageAsync(sessionId, sanitizedMessage, ct);

                // Sanitiza a resposta para prevenir XSS no frontend
                var safeResponse = InputSanitizer.SanitizeForDisplay(response);

                return Results.Ok(new
                {
                    response = safeResponse,
                    remaining = ChatRateLimiter.GetRemainingRequests(clientIp)
                });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error processing chat message");
                return Results.Json(new { error = "An internal error occurred processing your message." }, statusCode: 500);
            }
        });

        // GET /api/status — MCP server status + connected servers
        app.MapGet("/api/status", (HttpContext httpContext) =>
        {
            var clientIp = GetClientIp(httpContext);
            return Results.Ok(new
            {
                model,
                activeSessions = sessionManager.ActiveSessionCount,
                mcpServers = mcpManager.GetServerStatusList(),
                rateLimit = new
                {
                    remaining = ChatRateLimiter.GetRemainingRequests(clientIp),
                    maxPerMinute = 30
                }
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
            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId.Length > 100)
                return Results.BadRequest(new { error = "SessionId exceeds maximum length of 100 characters." });

            var sessionId = GetSessionId(request);
            sessionManager.CancelRequest(sessionId);
            return Results.Ok(new { success = true });
        });

        // POST /api/clear — clear conversation for a session
        app.MapPost("/api/clear", (ChatRequest request) =>
        {
            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId.Length > 100)
                return Results.BadRequest(new { error = "SessionId exceeds maximum length of 100 characters." });

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

    /// <summary>
    /// Obtém o IP real do cliente, respeitando proxies reversos (X-Forwarded-For).
    /// </summary>
    private static string GetClientIp(HttpContext context)
    {
        // Verifica X-Forwarded-For (proxy reverso / load balancer)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            // Pega o primeiro IP da lista (origem real)
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ips.Length > 0 && IPAddress.TryParse(ips[0], out _))
                return ips[0];
        }

        // Fallback para o IP direto da conexão
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
