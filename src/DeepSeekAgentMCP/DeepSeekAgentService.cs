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
            // --- Configuration via AgentHostBuilder ---
            var config = await AgentHostBuilder.LoadConfigAsync();

            _logger.LogInformation("Config file loaded: {ConfigPath}", PathHelper.FindConfigPath());

            // Validate configuration
            var configErrors = AgentHostBuilder.ValidateConfig(config);
            if (configErrors.Count > 0)
            {
                foreach (var error in configErrors)
                {
                    _logger.LogError("Configuration error: {Error}", error);
                }
                return;
            }

            // --- Initialize Agent ---
            // Create logger factory for DeepSeekClient (optional)
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                if (OperatingSystem.IsWindows())
                {
#pragma warning disable CA1416
                    builder.AddEventLog(settings =>
                    {
                        settings.SourceName = "DeepSeekAgentMCP";
                        settings.LogName = "Application";
                    });
#pragma warning restore CA1416
                }
            });
            var deepSeekClient = AgentHostBuilder.CreateClient(config, loggerFactory);
            var mcpManager = await AgentHostBuilder.CreateMcpManagerAsync(config, stoppingToken);
            var sessionManager = new SessionManager(deepSeekClient, mcpManager);

            _logger.LogInformation("MCP initialized successfully.");

            // --- Start Web Server via AgentHostBuilder ---
            _logger.LogInformation("Starting web interface at {Urls}", config.WebUrls);

            var app = AgentHostBuilder.BuildWebApplication(sessionManager, mcpManager, config, _logger);

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
}
