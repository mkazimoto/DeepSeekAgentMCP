using System.Text.Json;
using DeepSeekAgentMCP.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.Logging;

namespace DeepSeekAgentMCP;

/// <summary>
/// Factory para criar componentes do agente DeepSeek de forma centralizada.
/// Elimina duplicação de lógica de inicialização entre Program.cs e DeepSeekAgentService.cs.
/// </summary>
public static class AgentHostBuilder
{
    /// <summary>
    /// Loads and parses appsettings.json into an AgentConfig record.
    /// Falls back to environment variables for ApiKey if not present in config.
    /// </summary>
    public static async Task<AgentConfig> LoadConfigAsync(string? configPath = null)
    {
        configPath ??= PathHelper.FindConfigPath();

        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            return new AgentConfig
            {
                ApiKey = ResolveApiKey(string.Empty),
                Model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-flash",
                MaxTokens = 4096,
                Temperature = 0.7
            };
        }

        var configJson = await File.ReadAllTextAsync(configPath);
        using var doc = JsonDocument.Parse(configJson);

        if (!doc.RootElement.TryGetProperty("DeepSeek", out var deepSeekConfig))
        {
            return new AgentConfig
            {
                ApiKey = ResolveApiKey(string.Empty),
                Model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-flash",
                MaxTokens = 4096,
                Temperature = 0.7
            };
        }

        var apiKey = deepSeekConfig.TryGetProperty("ApiKey", out var apiKeyProp)
            ? apiKeyProp.GetString() ?? string.Empty
            : string.Empty;

        ThinkingConfig? thinkingConfig = null;
        if (deepSeekConfig.TryGetProperty("Thinking", out var thinkingProp))
        {
            var type = thinkingProp.GetProperty("type").GetString();
            if (!string.IsNullOrEmpty(type))
                thinkingConfig = new ThinkingConfig { Type = type };
        }

        var reasoningEffort = deepSeekConfig.TryGetProperty("ReasoningEffort", out var reasoningProp)
            ? reasoningProp.GetString()
            : null;

        var webEnabled = true;
        var webUrls = "http://localhost:5000";
        var launchBrowser = false;

        if (doc.RootElement.TryGetProperty("WebServer", out var webConfig))
        {
            webEnabled = webConfig.GetProperty("Enabled").GetBoolean();
            webUrls = webConfig.GetProperty("Urls").GetString() ?? "http://localhost:5000";
            launchBrowser = webConfig.GetProperty("LaunchBrowser").GetBoolean();
        }

        var mcpServerConfigPath = doc.RootElement.TryGetProperty("McpServerConfigPath", out var mcpPathProp)
            ? mcpPathProp.GetString() ?? "config/mcp-servers.json"
            : "config/mcp-servers.json";

        var rateLimit = 30;
        if (doc.RootElement.TryGetProperty("RateLimiting", out var rateLimitProp))
        {
            rateLimit = rateLimitProp.GetProperty("MaxRequestsPerMinute").GetInt32();
        }

        // --- CORS origins ---
        var allowedCorsOrigins = new List<string>();
        if (doc.RootElement.TryGetProperty("Cors", out var corsProp) &&
            corsProp.TryGetProperty("AllowedOrigins", out var originsProp))
        {
            foreach (var origin in originsProp.EnumerateArray())
            {
                var val = origin.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    allowedCorsOrigins.Add(val);
            }
        }

        // --- Auth config ---
        var requireAuth = false;
        string? authToken = null;
        if (doc.RootElement.TryGetProperty("Auth", out var authProp))
        {
            requireAuth = authProp.TryGetProperty("Enabled", out var authEnabledProp) && authEnabledProp.GetBoolean();
            authToken = authProp.TryGetProperty("Token", out var authTokenProp)
                ? authTokenProp.GetString()
                : null;
        }
        // Auth token via env var takes precedence
        authToken = Environment.GetEnvironmentVariable("DEEPSEEK_AGENT_AUTH_TOKEN") ?? authToken;

        // --- HTTPS config ---
        var httpsEnabled = false;
        string? httpsCertPath = null;
        string? httpsCertPassword = null;
        if (doc.RootElement.TryGetProperty("Https", out var httpsProp))
        {
            httpsEnabled = httpsProp.TryGetProperty("Enabled", out var httpsEnabledProp) && httpsEnabledProp.GetBoolean();
            httpsCertPath = httpsProp.TryGetProperty("CertificatePath", out var certPathProp)
                ? certPathProp.GetString()
                : null;
            // Cert password ONLY from env var, never from config file
            httpsCertPassword = Environment.GetEnvironmentVariable("DEEPSEEK_AGENT_HTTPS_PASSWORD");
        }

        var maxSessionsPerIp = 5;
        if (doc.RootElement.TryGetProperty("SessionLimits", out var sessionLimitsProp))
        {
            maxSessionsPerIp = sessionLimitsProp.TryGetProperty("MaxSessionsPerIp", out var maxSessProp)
                ? maxSessProp.GetInt32()
                : 5;
        }

        // --- Google OAuth config ---
        GoogleAuthConfig? googleAuth = null;
        if (doc.RootElement.TryGetProperty("GoogleAuth", out var googleAuthProp))
        {
            var googleEnabled = googleAuthProp.TryGetProperty("Enabled", out var gaEnabledProp) && gaEnabledProp.GetBoolean();
            if (googleEnabled)
            {
                // Priority 1: Environment variables
                var clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? string.Empty;
                var clientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? string.Empty;

                // Priority 2: Windows Registry (HKLM\SOFTWARE\DeepSeekAgentMCP)
                if (string.IsNullOrWhiteSpace(clientId) && OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DeepSeekAgentMCP");
                        if (regKey?.GetValue("GOOGLE_CLIENT_ID") is string regId && !string.IsNullOrWhiteSpace(regId))
                            clientId = regId;
                        if (regKey?.GetValue("GOOGLE_CLIENT_SECRET") is string regSecret && !string.IsNullOrWhiteSpace(regSecret))
                            clientSecret = regSecret;
                    }
                    catch { }
                }

                // Priority 3: Config file (appsettings.json)
                if (string.IsNullOrWhiteSpace(clientId) && googleAuthProp.TryGetProperty("ClientId", out var clientIdProp))
                    clientId = clientIdProp.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(clientSecret) && googleAuthProp.TryGetProperty("ClientSecret", out var clientSecretProp))
                    clientSecret = clientSecretProp.GetString() ?? string.Empty;

                var scopes = new List<string> { "openid", "profile", "email" };
                if (googleAuthProp.TryGetProperty("Scopes", out var scopesProp))
                {
                    scopes = scopesProp.EnumerateArray()
                        .Select(s => s.GetString() ?? string.Empty)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList()!;
                }

                googleAuth = new GoogleAuthConfig
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    Scopes = scopes,
                    Enabled = true
                };
            }
        }

        return new AgentConfig
        {
            ApiKey = ResolveApiKey(apiKey),
            Model = deepSeekConfig.TryGetProperty("Model", out var modelProp)
                ? modelProp.GetString() ?? "deepseek-v4-flash"
                : Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-flash",
            MaxTokens = deepSeekConfig.TryGetProperty("MaxTokens", out var maxTokensProp)
                ? maxTokensProp.GetInt32()
                : 4096,
            Temperature = deepSeekConfig.TryGetProperty("Temperature", out var tempProp)
                ? tempProp.GetDouble()
                : 0.7,
            ThinkingConfig = thinkingConfig,
            ReasoningEffort = reasoningEffort,
            McpServerConfigPath = mcpServerConfigPath,
            WebEnabled = webEnabled,
            WebUrls = webUrls,
            LaunchBrowser = launchBrowser,
            RateLimitPerMinute = rateLimit,
            AllowedCorsOrigins = allowedCorsOrigins,
            RequireAuth = requireAuth,
            AuthToken = authToken,
            HttpsEnabled = httpsEnabled,
            HttpsCertificatePath = httpsCertPath,
            MaxSessionsPerIp = maxSessionsPerIp,
            HttpClientTimeoutSeconds = doc.RootElement.TryGetProperty("HttpClient", out var httpClientProp) && httpClientProp.TryGetProperty("TimeoutSeconds", out var timeoutProp)
                ? timeoutProp.GetInt32()
                : 300,
            ContinueOnToolError = !doc.RootElement.TryGetProperty("ContinueOnToolError", out var continueOnErrorProp) || continueOnErrorProp.GetBoolean(),
            ParallelToolCalls = !doc.RootElement.TryGetProperty("ParallelToolCalls", out var parallelProp) || parallelProp.GetBoolean(),
            ToolDefinitionsCacheSeconds = doc.RootElement.TryGetProperty("ToolDefinitionsCacheSeconds", out var cacheProp)
                ? cacheProp.GetInt32()
                : 30,
            SummarizeHistory = doc.RootElement.TryGetProperty("SummarizeHistory", out var summarizeProp) && summarizeProp.GetBoolean(),
            GoogleAuth = googleAuth,
            ApiCommunicationLogPath = ResolveApiLogPath(doc),
            UserLogPath = ResolveUserLogPath(doc)
        };
    }

    /// <summary>
    /// Validates configuration and returns a list of validation errors.
    /// </summary>
    public static List<string> ValidateConfig(AgentConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ApiKey))
            errors.Add("DeepSeek API Key is not configured. Set DEEPSEEK_API_KEY environment variable or add ApiKey to appsettings.json.");

        if (string.IsNullOrWhiteSpace(config.Model))
            errors.Add("Model name is not configured.");

        if (config.MaxTokens <= 0)
            errors.Add("MaxTokens must be greater than 0.");

        if (config.Temperature is < 0 or > 2)
            errors.Add("Temperature must be between 0 and 2.");

        if (config.RateLimitPerMinute <= 0)
            errors.Add("RateLimitPerMinute must be greater than 0.");

        return errors;
    }

    /// <summary>
    /// Creates a DeepSeekClient from the agent configuration.
    /// </summary>
    public static DeepSeekClient CreateClient(AgentConfig config, ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger<DeepSeekClient>();
        logger?.LogInformation(
            "Creating DeepSeek client: Model={Model}, MaxTokens={MaxTokens}, Temperature={Temperature}",
            config.Model, config.MaxTokens, config.Temperature);

        return new DeepSeekClient(
            config.ApiKey,
            config.Model,
            config.MaxTokens,
            config.Temperature,
            config.ThinkingConfig,
            config.ReasoningEffort,
            logger,
            config.HttpClientTimeoutSeconds,
            apiCommunicationLogPath: config.ApiCommunicationLogPath);
    }

    /// <summary>
    /// Creates and initializes an McpToolManager from the agent configuration.
    /// </summary>
    public static async Task<McpToolManager> CreateMcpManagerAsync(AgentConfig config, CancellationToken cancellationToken = default)
    {
        var configPath = PathHelper.FindConfigPath();
        var mcpConfigFullPath = PathHelper.FindMcpConfigPath(configPath, config.McpServerConfigPath);

        var mcpManager = new McpToolManager(mcpConfigFullPath);
        await mcpManager.InitializeAsync(cancellationToken);
        return mcpManager;
    }

    /// <summary>
    /// Creates a web application configured to serve the agent's API and static files.
    /// </summary>
    public static WebApplication BuildWebApplication(
        SessionManager sessionManager,
        McpToolManager mcpManager,
        AgentConfig config,
        UserLogger? userLogger = null,
        ILogger? logger = null)
    {
        var contentRoot = PathHelper.FindContentRoot();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = contentRoot,
            WebRootPath = "wwwroot"
        });

        // --- CORS ---
        if (config.AllowedCorsOrigins.Count > 0)
        {
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.WithOrigins([.. config.AllowedCorsOrigins])
                          .WithMethods("GET", "POST")
                          .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                          .AllowCredentials();
                });
            });
            logger?.LogInformation("CORS configured with {Count} allowed origins", config.AllowedCorsOrigins.Count);
        }

        // --- HTTPS ---
        if (config.HttpsEnabled)
        {
            builder.WebHost.UseKestrel(options =>
            {
                options.ConfigureHttpsDefaults(httpsOptions =>
                {
                    if (!string.IsNullOrEmpty(config.HttpsCertificatePath))
                    {
                        var password = Environment.GetEnvironmentVariable("DEEPSEEK_AGENT_HTTPS_PASSWORD");
                        if (!string.IsNullOrEmpty(password))
                        {
                            httpsOptions.ServerCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                                System.IO.File.ReadAllBytes(config.HttpsCertificatePath), password);
                        }
                        else
                        {
                            httpsOptions.ServerCertificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(config.HttpsCertificatePath);
                        }
                    }
                });
            });
            logger?.LogInformation("HTTPS enabled");
        }

        // --- Google OAuth + Cookie Authentication ---
        if (config.GoogleAuth is { Enabled: true, ClientId.Length: > 0, ClientSecret.Length: > 0 })
        {
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "deepseek-agent-auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = "/api/auth/google/login";
                options.LogoutPath = "/api/auth/logout";
                options.AccessDeniedPath = "/";
            })
            .AddGoogle(options =>
            {
                options.ClientId = config.GoogleAuth.ClientId;
                options.ClientSecret = config.GoogleAuth.ClientSecret;
                options.Scope.Clear();
                foreach (var scope in config.GoogleAuth.Scopes)
                    options.Scope.Add(scope);
                options.SaveTokens = true;
                options.CallbackPath = "/api/auth/google/callback";
                options.ClaimActions.MapJsonKey("picture", "picture");
                options.Events = new Microsoft.AspNetCore.Authentication.OAuth.OAuthEvents
                {
                    OnCreatingTicket = context =>
                    {
                        context.Identity?.AddClaim(new System.Security.Claims.Claim(
                            "login_timestamp",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
                        return System.Threading.Tasks.Task.CompletedTask;
                    }
                };
            });

            builder.Services.AddAuthorization();

            logger?.LogInformation("Google OAuth authentication enabled");
        }

        builder.WebHost.UseUrls(config.WebUrls);
        var app = builder.Build();

        if (config.AllowedCorsOrigins.Count > 0)
        {
            app.UseCors();
        }

        // --- Authentication middleware (only if Google auth is enabled) ---
        if (config.GoogleAuth is { Enabled: true, ClientId.Length: > 0, ClientSecret.Length: > 0 })
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseStaticFiles();

        app.MapAgentEndpoints(sessionManager, mcpManager, config, userLogger, logger);

        return app;
    }

    /// <summary>
    /// Resolves the API communication log path from config, making it absolute using the content root.
    /// Returns null if not configured.
    /// </summary>
    private static string? ResolveApiLogPath(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("ApiCommunicationLogPath", out var apiLogProp))
            return null;

        var relativePath = apiLogProp.GetString();
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var contentRoot = PathHelper.FindContentRoot();
        return Path.GetFullPath(Path.Combine(contentRoot, relativePath));
    }

    /// <summary>
    /// Resolves the user activity log directory from config, making it absolute using the content root.
    /// Defaults to "logs" (directory) if enabled but not specified.
    /// The UserLogger will create daily files inside this directory (user-log-YYYY-MM-DD.json).
    /// Returns null if logging is disabled.
    /// </summary>
    private static string? ResolveUserLogPath(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("UserLog", out var userLogProp))
            return null;

        var enabled = userLogProp.TryGetProperty("Enabled", out var enabledProp) && enabledProp.GetBoolean();
        if (!enabled)
            return null;

        var relativePath = userLogProp.TryGetProperty("LogPath", out var pathProp)
            ? pathProp.GetString()
            : null;

        var contentRoot = PathHelper.FindContentRoot();

        if (string.IsNullOrWhiteSpace(relativePath))
            return Path.GetFullPath(Path.Combine(contentRoot, "logs"));

        return Path.GetFullPath(Path.Combine(contentRoot, relativePath));
    }

    /// Resolves the DeepSeek API key with inverted precedence:
    /// environment variables FIRST (Process scope, then User scope),
    /// then falls back to Windows Registry, then config file as last resort.
    /// </summary>
    private static string ResolveApiKey(string configApiKey)
    {
        // Priority 1: Process-level environment variable
        var envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        // Priority 2: User-level environment variable (Windows only)
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User);
#pragma warning restore CA1416
            if (!string.IsNullOrWhiteSpace(envKey))
                return envKey;
        }

        // Priority 3: Windows Registry (HKLM\SOFTWARE\DeepSeekAgentMCP)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DeepSeekAgentMCP");
                if (key?.GetValue("DEEPSEEK_API_KEY") is string regValue && !string.IsNullOrWhiteSpace(regValue))
                    return regValue;
            }
            catch
            {
                // Ignore registry errors
            }
        }

        // Priority 4: Config file (last resort — may be versioned)
        if (!string.IsNullOrWhiteSpace(configApiKey))
        {
            System.Console.WriteLine("[WARNING] DeepSeek API Key loaded from config file. Consider using DEEPSEEK_API_KEY environment variable for better security.");
            return configApiKey;
        }

        return string.Empty;
    }
}
