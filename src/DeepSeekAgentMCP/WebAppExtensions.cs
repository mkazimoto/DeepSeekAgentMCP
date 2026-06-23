using System.Net;
using System.Security.Claims;
using DeepSeekAgentMCP.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using System.Collections.Concurrent;

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

    // Profile picture cache: userId+version -> (bytes, contentType)
    private static readonly ConcurrentDictionary<string, (byte[] Data, string ContentType)> _pictureCache = new();
    private static readonly HttpClient _pictureHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "DeepSeekAgentMCP/1.0" } }
    };

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
        return MapAgentEndpoints(app, sessionManager, mcpManager, config.Model, config.RateLimitPerMinute, config.RequireAuth, config.AuthToken, config.MaxSessionsPerIp, logger, config.GoogleAuth);
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
        ILogger? logger = null,
        GoogleAuthConfig? googleAuth = null)
    {
        var googleAuthEnabled = googleAuth is { Enabled: true, ClientId.Length: > 0, ClientSecret.Length: > 0 };

        // --- Helper to check authentication (token OR Google cookie) ---
        static IResult? CheckAuth(HttpContext httpContext, bool requireAuth, string? authToken, bool googleAuthEnabled)
        {
            // If no auth is required at all, allow
            if (!requireAuth && !googleAuthEnabled)
                return null;

            // Check Google cookie auth first
            if (googleAuthEnabled && httpContext.User.Identity?.IsAuthenticated == true)
                return null;

            // Check token auth
            if (requireAuth && !string.IsNullOrEmpty(authToken))
            {
                var provided = httpContext.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "")
                    ?? httpContext.Request.Headers["X-API-Key"].FirstOrDefault();

                if (!string.IsNullOrEmpty(provided) && string.Equals(provided, authToken, StringComparison.Ordinal))
                    return null;
            }

            // If only Google auth is enabled (no token auth), allow if Google auth passes
            if (googleAuthEnabled && !requireAuth)
            {
                // User is not authenticated via Google cookie
                httpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "Unauthorized. Please sign in with Google first." }, statusCode: 401);
            }

            httpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Json(new { error = "Unauthorized. Provide a valid API key via Authorization: Bearer header or X-API-Key header." }, statusCode: 401);
        }

        // POST /api/chat — send a message (com rate limiting e sanitização)
        app.MapPost("/api/chat", async (HttpContext httpContext, ChatRequest request, CancellationToken ct) =>
        {
            // --- Autenticação ---
            var authResult = CheckAuth(httpContext, requireAuth, authToken, googleAuthEnabled);
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

                // NOTA: A resposta NÃO passa por SanitizeForDisplay aqui porque:
                // 1. A serialização JSON já faz escape de caracteres HTML automaticamente
                // 2. O marked.parse() no frontend já renderiza HTML com segurança
                // 3. A SanitizeForDisplay tem uma regex (on\w+\s*=) que corrompe SQL
                //    contendo cláusulas ON seguidas de = (ex: INNER JOIN T2 ON T2.COL = T1.COL)
                return Results.Ok(new
                {
                    response,
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
            var authResult = CheckAuth(httpContext, requireAuth, authToken, googleAuthEnabled);
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
            var authResult = CheckAuth(httpContext, requireAuth, authToken, googleAuthEnabled);
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
            var authResult = CheckAuth(httpContext, requireAuth, authToken, googleAuthEnabled);
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
            var authResult = CheckAuth(httpContext, requireAuth, authToken, googleAuthEnabled);
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
            var authResult = CheckAuth(httpContext, requireAuth, authToken, googleAuthEnabled);
            if (authResult != null) return authResult;

            return Results.Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                activeSessions = sessionManager.ActiveSessionCount,
                connectedUsers = sessionManager.UniqueClientCount
            });
        });

        // ============================================================
        //  Google OAuth Endpoints
        // ============================================================
        if (googleAuthEnabled)
        {
            // GET /api/auth/status — check current authentication status
            app.MapGet("/api/auth/status", (HttpContext httpContext) =>
            {
                var user = httpContext.User;
                if (user.Identity?.IsAuthenticated == true)
                {
                    // Tentar múltiplos claim types para picture (o Google pode usar diferentes
                    // claim types dependendo da versão do ASP.NET Core / Google handler)
                    var pictureClaim = user.FindFirst("picture")?.Value
                        ?? user.FindFirst("urn:google:picture")?.Value
                        ?? user.FindFirst("urn:google:image")?.Value
                        ?? user.FindFirst("image")?.Value;

                    var loginTimestamp = user.FindFirst("login_timestamp")?.Value ?? "0";
                    var pictureUrl = pictureClaim != null
                        ? $"/api/auth/profile-picture?v={loginTimestamp}"
                        : null;

                    return Results.Ok(new
                    {
                        authenticated = true,
                        name = user.FindFirst(ClaimTypes.Name)?.Value,
                        email = user.FindFirst(ClaimTypes.Email)?.Value,
                        picture = pictureUrl
                    });
                }

                return Results.Ok(new { authenticated = false });
            });

            // GET /api/auth/google/login — trigger Google OAuth challenge
            app.MapGet("/api/auth/google/login", async (HttpContext httpContext) =>
            {
                // If already authenticated, redirect to home
                if (httpContext.User.Identity?.IsAuthenticated == true)
                    return Results.Redirect("/");

                // Challenge with Google — after auth, redirect back to /
                var redirectUrl = "/";
                var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
                {
                    RedirectUri = redirectUrl
                };
                await httpContext.ChallengeAsync(GoogleDefaults.AuthenticationScheme, properties);
                return Results.Empty;
            });

            // GET /api/auth/profile-picture — proxy que baixa a foto do Google e cacheia no servidor
            app.MapGet("/api/auth/profile-picture", async (HttpContext httpContext) =>
            {
                var user = httpContext.User;
                if (user.Identity?.IsAuthenticated != true)
                    return Results.Unauthorized();

                var pictureClaim = user.FindFirst("picture")?.Value
                    ?? user.FindFirst("urn:google:picture")?.Value
                    ?? user.FindFirst("urn:google:image")?.Value
                    ?? user.FindFirst("image")?.Value;

                if (string.IsNullOrEmpty(pictureClaim))
                    return Results.NotFound();

                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? user.FindFirst(ClaimTypes.Email)?.Value
                    ?? "default";

                var version = httpContext.Request.Query["v"].FirstOrDefault() ?? "0";
                var cacheKey = $"profile_pic_{userId}_{version}";

                // Try server cache first
                if (_pictureCache.TryGetValue(cacheKey, out var cached))
                {
                    httpContext.Response.Headers.CacheControl = "private, max-age=3600";
                    return Results.File(cached.Data, cached.ContentType);
                }

                // Fetch from Google
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var response = await _pictureHttpClient.GetAsync(pictureClaim, cts.Token);
                    if (!response.IsSuccessStatusCode)
                        return Results.NotFound();

                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    var data = await response.Content.ReadAsByteArrayAsync(cts.Token);

                    _pictureCache[cacheKey] = (data, contentType);

                    httpContext.Response.Headers.CacheControl = "private, max-age=3600";
                    return Results.File(data, contentType);
                }
                catch
                {
                    // Se falhou, tenta servir o cache anterior (sem versão) como fallback
                    var fallbackKey = $"profile_pic_{userId}_0";
                    if (_pictureCache.TryGetValue(fallbackKey, out var fallback))
                    {
                        httpContext.Response.Headers.CacheControl = "private, max-age=60";
                        return Results.File(fallback.Data, fallback.ContentType);
                    }
                    return Results.NotFound();
                }
            });

            // POST /api/auth/logout — sign out and close session
            app.MapPost("/api/auth/logout", async (HttpContext httpContext, string? sessionId) =>
            {
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                if (!string.IsNullOrEmpty(sessionId) && sessionId.Length <= 100)
                {
                    sessionManager.RemoveSession(sessionId);
                }

                return Results.Ok(new { success = true });
            });
        }
        else
        {
            // Auth not enabled — return simple status
            app.MapGet("/api/auth/status", () => Results.Ok(new { authenticated = false, authDisabled = true }));
        }

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
