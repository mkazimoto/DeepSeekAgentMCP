using System.Text.Json;
using DeepSeekAgentMCP.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeepSeekAgentMCP;

/// <summary>
/// Windows Service que hospeda o agente DeepSeek com MCP.
/// Executa o servidor web em segundo plano, sem interação com console.
/// </summary>
public class DeepSeekAgentService : BackgroundService
{
    private readonly ILogger<DeepSeekAgentService> _logger;

    public DeepSeekAgentService(ILogger<DeepSeekAgentService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeepSeek Agent Service starting...");

        try
        {
            // --- Configuration ---
            // Try published mode first (config next to executable), then development mode
            var configPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "appsettings.json"))
            };

            var configPath = configPaths.FirstOrDefault(File.Exists) ?? string.Empty;

            string apiKey;
            string model;
            int maxTokens;
            double temperature;
            string webUrls = "http://localhost:5000";

            ThinkingConfig? thinkingConfig = null;
            string? reasoningEffort = null;

            if (!string.IsNullOrEmpty(configPath))
            {
                var configJson = await File.ReadAllTextAsync(configPath, stoppingToken);
                using var doc = JsonDocument.Parse(configJson);
                var deepSeekConfig = doc.RootElement.GetProperty("DeepSeek");

                apiKey = deepSeekConfig.TryGetProperty("ApiKey", out var apiKeyProp)
                    ? apiKeyProp.GetString() ?? string.Empty
                    : string.Empty;
                model = deepSeekConfig.GetProperty("Model").GetString() ?? "deepseek-v4-flash";
                maxTokens = deepSeekConfig.GetProperty("MaxTokens").GetInt32();
                temperature = deepSeekConfig.GetProperty("Temperature").GetDouble();

                if (deepSeekConfig.TryGetProperty("Thinking", out var thinkingProp))
                {
                    var type = thinkingProp.GetProperty("type").GetString();
                    if (!string.IsNullOrEmpty(type))
                        thinkingConfig = new ThinkingConfig { Type = type };
                }

                if (deepSeekConfig.TryGetProperty("ReasoningEffort", out var reasoningProp))
                {
                    reasoningEffort = reasoningProp.GetString();
                }

                if (doc.RootElement.TryGetProperty("WebServer", out var webConfig))
                {
                    webUrls = webConfig.GetProperty("Urls").GetString() ?? "http://localhost:5000";
                }

                _logger.LogInformation("Config file loaded: {ConfigPath}", configPath);
            }
            else
            {
                apiKey = GetEnvDeepSeekApiKey();
                model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-flash";
                maxTokens = 4096;
                temperature = 0.7;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = GetEnvDeepSeekApiKey();
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("DeepSeek API Key not found. Set DEEPSEEK_API_KEY environment variable or configure ApiKey in appsettings.json.");
                return;
            }

            // --- Initialize Agent ---
            // MCP config path: try next to config file first, then relative to project root
            var configDir = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
            var mcpConfigFullPath = Path.Combine(configDir, "mcp-servers.json");

            if (!File.Exists(mcpConfigFullPath))
            {
                var mcpConfigRelPath = "config/mcp-servers.json";
                if (File.Exists(configPath))
                {
                    using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, stoppingToken));
                    mcpConfigRelPath = doc2.RootElement.GetProperty("McpServerConfigPath").GetString() ?? "config/mcp-servers.json";
                }
                var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? Directory.GetCurrentDirectory();
                mcpConfigFullPath = Path.GetFullPath(Path.Combine(projectRoot, mcpConfigRelPath));
            }

            var deepSeekClient = new DeepSeekClient(apiKey, model, maxTokens, temperature, thinkingConfig, reasoningEffort);
            var mcpManager = new McpToolManager(mcpConfigFullPath);
            var sessionManager = new SessionManager(deepSeekClient, mcpManager);

            await mcpManager.InitializeAsync(stoppingToken);

            _logger.LogInformation("MCP initialized successfully.");

            // --- Start Web Server ---
            _logger.LogInformation("Starting web interface at {Urls}", webUrls);

            // Content root is the base directory (where config/ and wwwroot/ live)
            var contentRoot = Path.GetFullPath(AppContext.BaseDirectory);
            // In development, content root is the project root
            if (!Directory.Exists(Path.Combine(contentRoot, "wwwroot")))
            {
                var devPath = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", ".."));
                if (Directory.Exists(Path.Combine(devPath, "wwwroot")))
                    contentRoot = devPath;
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = contentRoot,
                WebRootPath = "wwwroot"
            });

            // Configure logging for the web host
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                builder.Logging.AddEventLog(settings =>
                {
                    settings.SourceName = "DeepSeekAgentMCP";
                    settings.LogName = "Application";
                });
#pragma warning restore CA1416
            }

            builder.WebHost.UseUrls(webUrls);
            var app = builder.Build();
            app.UseStaticFiles();

            // Register all agent API endpoints via shared extension method
            app.MapAgentEndpoints(sessionManager, mcpManager, model, _logger);

            _logger.LogInformation("DeepSeek Agent Service started successfully. Active sessions: {Count}", sessionManager.ActiveSessionCount);

            await app.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DeepSeek Agent Service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in DeepSeek Agent Service");
            throw;
        }
    }

    private static string GetEnvDeepSeekApiKey()
    {
        return Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User)
            ?? string.Empty;
    }


}
