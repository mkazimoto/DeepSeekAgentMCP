using System.Text.Json;
using DeepSeekAgentMCP.Models;
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
        var deepSeekConfig = doc.RootElement.GetProperty("DeepSeek");

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

        return new AgentConfig
        {
            ApiKey = ResolveApiKey(apiKey),
            Model = deepSeekConfig.GetProperty("Model").GetString() ?? "deepseek-v4-flash",
            MaxTokens = deepSeekConfig.GetProperty("MaxTokens").GetInt32(),
            Temperature = deepSeekConfig.GetProperty("Temperature").GetDouble(),
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
            SummarizeHistory = doc.RootElement.TryGetProperty("SummarizeHistory", out var summarizeProp) && summarizeProp.GetBoolean()
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
            config.HttpClientTimeoutSeconds);
    }

    /// <summary>
    /// Creates and initializes an McpToolManager from the agent configuration.
    /// </summary>
    public static async Task<McpToolManager> CreateMcpManagerAsync(AgentConfig config, CancellationToken cancellationToken = default)
    {
        // FindMcpConfigPath expects the path to appsettings.json to locate mcp-servers.json next to it
        var configPath = PathHelper.FindConfigPath();
        var mcpConfigFullPath = PathHelper.FindMcpConfigPath(configPath);

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

        builder.WebHost.UseUrls(config.WebUrls);
        var app = builder.Build();

        if (config.AllowedCorsOrigins.Count > 0)
        {
            app.UseCors();
        }

        app.UseStaticFiles();

        app.MapAgentEndpoints(sessionManager, mcpManager, config, logger);

        return app;
    }

    /// <summary>
    /// Resolves the DeepSeek API key with inverted precedence:
    /// environment variables FIRST (Process scope, then User scope),
    /// then falls back to config file as last resort.
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

        // Priority 3: Config file (last resort — may be versioned)
        if (!string.IsNullOrWhiteSpace(configApiKey))
        {
            System.Console.WriteLine("[WARNING] DeepSeek API Key loaded from config file. Consider using DEEPSEEK_API_KEY environment variable for better security.");
            return configApiKey;
        }

        return string.Empty;
    }
}
