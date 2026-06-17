using System.Net;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Extension methods for WebApplication to register common agent API endpoints.
/// Elimina duplicação de rotas entre Program.cs e DeepSeekAgentService.cs.
/// </summary>
public static class WebAppExtensions
{
    // Rate limiter: sliding window por IP (lazy, thread-safe)
    private static readonly Lazy<RateLimiter> _chatRateLimiter = new(() =>
        new RateLimiter(maxRequests: 30, windowSize: TimeSpan.FromMinutes(1)));

    /// <summary>
    /// Maps all common agent API endpoints with config from AgentConfig.
    /// </summary>
    public static WebApplication MapAgentEndpoints(
        this WebApplication app,
        SessionManager sessionManager,
        McpToolManager mcpManager,
        AgentConfig config,
        ILogger? logger = null)
    {
        return MapAgentEndpoints(app, sessionManager, mcpManager, config.Model, config.RateLimitPerMinute, config.RequireAuth, config.AuthToken, config.MaxSessionsPerIp, logger);
    }

    /// <summary>
    /// Maps all common agent API endpoints with granular parameters.
    /// </summary>
    public static WebApplication MapAgentEndpoints(
        this WebApplication app,
        SessionManager sessionManager,
        McpToolManager mcpManager,
        string model,
        int maxRequestsPerMinute,
        bool requireAuth,
        string? authToken,
        int maxSessionsPerIp,
        ILogger? logger = null)
    {
        // --- Helper to check authentication ---
        static IResult? CheckAuth(HttpContext httpContext, bool requireAuth, string? authToken)
        {
            if (!requireAuth || string.IsNullOrEmpty(authToken))
                return null;

            var provided = httpContext.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "")
                ?? httpContext.Request.Headers["X-API-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(provided) || !string.Equals(provided, authToken, StringComparison.Ordinal))
            {
                httpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "Unauthorized. Provide a valid API key via Authorization: Bearer header or X-API-Key header." }, statusCode: 401);
            }

            return null;
        }

        // POST /api/chat — send a message (com rate limiting e sanitização)
        app.MapPost("/api/chat", async (HttpContext httpContext, ChatRequest request, CancellationToken ct) =>
        {
            // --- Autenticação ---
            var authResult = CheckAuth(httpContext, requireAuth, authToken);
            if (authResult != null) return authResult;

            // --- Validações básicas ---
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message is required." });

            if (request.Message.Length > 10000)
                return Results.BadRequest(new { error = "Message exceeds maximum length of 10000 characters." });

            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId.Length > 100)
                return Results.BadRequest(new { error = "SessionId exceeds maximum length of 100 characters." });

            // --- Rate limiting por IP ---
            var clientIp = GetClientIp(httpContext);
            if (!_chatRateLimiter.Value.TryConsume(clientIp))
            {
                logger?.LogWarning("Rate limit exceeded for IP: {ClientIp}", clientIp);
                var retryAfter = TimeSpan.FromMinutes(1);
                httpContext.Response.Headers.RetryAfter = retryAfter.TotalSeconds.ToString();
                return Results.Json(
                    new { error = "Too many requests. Please wait before sending another message.", retryAfterSeconds = (int)retryAfter.TotalSeconds },
                    statusCode: (int)HttpStatusCode.TooManyRequests);
            }

            // --- Limite de sessões por IP ---
            var sessionId = GetSessionId(request);
            if (!string.IsNullOrEmpty(clientIp) && clientIp != "unknown" && clientIp != "::1")
            {
                var sessionCount = sessionManager.GetSessionCountForIp(clientIp);
                if (sessionCount >= maxSessionsPerIp && !sessionManager.SessionExists(sessionId))
                {
                    logger?.LogWarning("Session limit exceeded for IP: {ClientIp} ({Count}/{Max})", clientIp, sessionCount, maxSessionsPerIp);
                    return Results.Json(
                        new { error = $"Too many active sessions from this IP. Maximum is {maxSessionsPerIp}. Please clear an existing session or wait for cleanup." },
                        statusCode: (int)HttpStatusCode.TooManyRequests);
                }
            }

            // --- Sanitização da mensagem ---
            var sanitizedMessage = InputSanitizer.SanitizeMessage(request.Message);

            // Se após sanitizar ficou vazio, rejeitar
            if (string.IsNullOrWhiteSpace(sanitizedMessage))
                return Results.BadRequest(new { error = "Message contains no valid content after sanitization." });

            try
            {
                var response = await sessionManager.ProcessMessageAsync(sessionId, sanitizedMessage, clientIp, ct);

                // Sanitiza a resposta para prevenir XSS no frontend
                var safeResponse = InputSanitizer.SanitizeForDisplay(response);

                return Results.Ok(new
                {
                    response = safeResponse,
                    remaining = _chatRateLimiter.Value.GetRemainingRequests(clientIp)
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
            var authResult = CheckAuth(httpContext, requireAuth, authToken);
            if (authResult != null) return authResult;

            var clientIp = GetClientIp(httpContext);
            return Results.Ok(new
            {
                model,
                activeSessions = sessionManager.ActiveSessionCount,
                mcpServers = mcpManager.GetServerStatusList(),
                rateLimit = new
                {
                    remaining = _chatRateLimiter.Value.GetRemainingRequests(clientIp),
                    maxPerMinute = maxRequestsPerMinute
                }
            });
        });

        // GET /api/history — conversation history for a session
        app.MapGet("/api/history", (HttpContext httpContext, string? sessionId) =>
        {
            var authResult = CheckAuth(httpContext, requireAuth, authToken);
            if (authResult != null) return authResult;

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
        app.MapPost("/api/cancel", (HttpContext httpContext, ChatRequest request) =>
        {
            var authResult = CheckAuth(httpContext, requireAuth, authToken);
            if (authResult != null) return authResult;

            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId.Length > 100)
                return Results.BadRequest(new { error = "SessionId exceeds maximum length of 100 characters." });

            var sessionId = GetSessionId(request);
            sessionManager.CancelRequest(sessionId);
            return Results.Ok(new { success = true });
        });

        // POST /api/clear — clear conversation for a session
        app.MapPost("/api/clear", async (HttpContext httpContext, ChatRequest request) =>
        {
            var authResult = CheckAuth(httpContext, requireAuth, authToken);
            if (authResult != null) return authResult;

            if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId.Length > 100)
                return Results.BadRequest(new { error = "SessionId exceeds maximum length of 100 characters." });

            var sessionId = GetSessionId(request);
            await sessionManager.ClearConversationAsync(sessionId);
            return Results.Ok(new { success = true });
        });

        // GET /api/health — liveness/readiness probe
        app.MapGet("/api/health", (HttpContext httpContext) =>
        {
            var authResult = CheckAuth(httpContext, requireAuth, authToken);
            if (authResult != null) return authResult;

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
