using System.Text.Json;
using System.Text.RegularExpressions;
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

            if (!string.IsNullOrEmpty(configPath))
            {
                var configJson = await File.ReadAllTextAsync(configPath, stoppingToken);
                using var doc = JsonDocument.Parse(configJson);
                var deepSeekConfig = doc.RootElement.GetProperty("DeepSeek");

                apiKey = deepSeekConfig.TryGetProperty("ApiKey", out var apiKeyProp)
                    ? apiKeyProp.GetString() ?? string.Empty
                    : string.Empty;
                model = deepSeekConfig.GetProperty("Model").GetString() ?? "deepseek-chat";
                maxTokens = deepSeekConfig.GetProperty("MaxTokens").GetInt32();
                temperature = deepSeekConfig.GetProperty("Temperature").GetDouble();

                if (doc.RootElement.TryGetProperty("WebServer", out var webConfig))
                {
                    webUrls = webConfig.GetProperty("Urls").GetString() ?? "http://localhost:5000";
                }

                _logger.LogInformation("Config file loaded: {ConfigPath}", configPath);
            }
            else
            {
                apiKey = GetEnvDeepSeekApiKey();
                model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-chat";
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

            var deepSeekClient = new DeepSeekClient(apiKey, model, maxTokens, temperature);
            var mcpManager = new McpToolManager(mcpConfigFullPath);
            var agent = new DeepSeekAgent(deepSeekClient, mcpManager);

            await agent.InitializeAsync(stoppingToken);

            _logger.LogInformation("MCP Status: {Status}", agent.GetMcpStatus());

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

            // POST /api/chat — send a message
            app.MapPost("/api/chat", async (ChatRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                    return Results.BadRequest(new { error = "Message is required." });

                try
                {
                    var response = await agent.ProcessMessageAsync(request.Message, stoppingToken);
                    return Results.Ok(new { response });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing chat message");
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });

            // GET /api/status — MCP server status
            app.MapGet("/api/status", () =>
            {
                var status = agent.GetMcpStatus();
                var servers = ParseMcpStatus(status);
                return Results.Ok(new
                {
                    model = model,
                    mcpServers = servers
                });
            });

            // GET /api/history — conversation history
            app.MapGet("/api/history", () =>
            {
                var history = agent.GetConversationHistory();
                var messages = history.Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    name = m.Name
                });
                return Results.Ok(new { history = messages });
            });

            // POST /api/clear — clear conversation
            app.MapPost("/api/clear", () =>
            {
                agent.ClearConversation();
                return Results.Ok(new { success = true });
            });

            app.MapFallbackToFile("index.html");

            _logger.LogInformation("DeepSeek Agent Service started successfully.");

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

    private static List<object> ParseMcpStatus(string status)
    {
        var servers = new List<object>();
        if (string.IsNullOrEmpty(status) || status == "No MCP servers connected.")
            return servers;

        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('-'))
            {
                var parts = trimmed.TrimStart('-', ' ').Split(':');
                if (parts.Length >= 2)
                {
                    var name = parts[0].Trim();
                    var toolPart = parts[1].Trim();
                    var toolCount = 0;
                    var match = Regex.Match(toolPart, @"(\d+)");
                    if (match.Success)
                        int.TryParse(match.Groups[1].Value, out toolCount);

                    servers.Add(new
                    {
                        name,
                        connected = true,
                        toolCount
                    });
                }
            }
        }
        return servers;
    }
}
