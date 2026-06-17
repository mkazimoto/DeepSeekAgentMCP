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
            RateLimitPerMinute = rateLimit
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
            logger);
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

        builder.WebHost.UseUrls(config.WebUrls);
        var app = builder.Build();
        app.UseStaticFiles();

        app.MapAgentEndpoints(sessionManager, mcpManager, config.Model, config.RateLimitPerMinute, logger);

        return app;
    }

    /// <summary>
    /// Attempts to resolve the DeepSeek API key from config first,
    /// then falls back to environment variables (Process scope, then User scope).
    /// </summary>
    private static string ResolveApiKey(string configApiKey)
    {
        if (!string.IsNullOrWhiteSpace(configApiKey))
            return configApiKey;

        var envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User);
#pragma warning restore CA1416
            if (!string.IsNullOrWhiteSpace(envKey))
                return envKey;
        }

        return string.Empty;
    }
}
