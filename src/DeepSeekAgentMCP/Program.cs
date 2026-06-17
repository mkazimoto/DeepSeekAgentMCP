using DeepSeekAgentMCP;
using DeepSeekAgentMCP.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ============================================================
//  DeepSeek Agent with MCP (Model Context Protocol) Support
//  Console + Web Interface + Windows Service
// ============================================================

// Check if running as a Windows Service
var isService = args.Contains("--service");

if (isService)
{
    await RunAsServiceAsync();
}
else
{
    await RunAsConsoleAsync(isService);
}

return;

// ===================================================================
//  Windows Service Mode
// ===================================================================
static async Task RunAsServiceAsync()
{
    var host = Host.CreateDefaultBuilder()
        .UseWindowsService(options =>
        {
            options.ServiceName = "DeepSeekAgentMCP";
        })
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                logging.AddEventLog(settings =>
                {
                    settings.SourceName = "DeepSeekAgentMCP";
                    settings.LogName = "Application";
                });
#pragma warning restore CA1416
            }
        })
        .ConfigureServices(services =>
        {
            services.AddHostedService<DeepSeekAgentService>();
        })
        .Build();

    await host.RunAsync();
}

// ===================================================================
//  Console Interactive Mode
// ===================================================================
static async Task RunAsConsoleAsync(bool isService)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(@"DeepSeek Agent with MCP Support");
    Console.ResetColor();
    Console.WriteLine("=== DeepSeek Agent with MCP Support ===\n");

    // --- Configuration via AgentHostBuilder ---
    var config = await AgentHostBuilder.LoadConfigAsync();

    Console.WriteLine($"Config file: {PathHelper.FindConfigPath()}");

    // Validate configuration
    var configErrors = AgentHostBuilder.ValidateConfig(config);
    if (configErrors.Count > 0)
    {
        foreach (var error in configErrors)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n\u26a0 {error}");
            Console.ResetColor();
        }
    }

    // Prompt for API key if still missing
    if (string.IsNullOrWhiteSpace(config.ApiKey))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n\u26a0 DeepSeek API Key not found!");
        Console.WriteLine("Please set it in one of the following ways:");
        Console.WriteLine("  1. Edit config/appsettings.json and add your ApiKey");
        Console.WriteLine("  2. Set environment variable: DEEPSEEK_API_KEY");
        Console.ResetColor();
        Console.Write("\nEnter your DeepSeek API Key: ");
        var manualKey = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(manualKey))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No API key provided. Exiting.");
            Console.ResetColor();
            return;
        }

        config = config with { ApiKey = manualKey };
    }

    // --- Initialize Agent ---
    var mcpManager = await AgentHostBuilder.CreateMcpManagerAsync(config);
    var deepSeekClient = AgentHostBuilder.CreateClient(config);
    var agent = new DeepSeekAgent(deepSeekClient, mcpManager);

    // MCP já foi inicializado por CreateMcpManagerAsync

    Console.WriteLine(agent.GetMcpStatus());
    Console.WriteLine();

    // --- Start Web Server or Console Mode ---
    if (config.WebEnabled)
    {
        var sessionManager = new SessionManager(deepSeekClient, mcpManager);
        await RunWebServerAsync(sessionManager, mcpManager, config, isService);
    }
    else
    {
        await RunConsoleModeAsync(agent);
    }
}

// ===================================================================
//  Web Server Mode (ASP.NET Core Minimal API)
// ===================================================================
static async Task RunWebServerAsync(SessionManager sessionManager, McpToolManager mcpManager, AgentConfig config, bool isService)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n Starting web interface at {config.WebUrls}");
    Console.ResetColor();

    var app = AgentHostBuilder.BuildWebApplication(sessionManager, mcpManager, config);

    // Only launch browser in development/local environment, never in service or production mode
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
    var canLaunchBrowser = config.LaunchBrowser
        && !isService
        && string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

    if (canLaunchBrowser)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = config.WebUrls,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    await app.RunAsync();
}

// ===================================================================
//  Console Mode
// ===================================================================
static async Task RunConsoleModeAsync(DeepSeekAgent agent)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Interactive mode ready! Type your messages below.");
    Console.WriteLine("Commands:");
    Console.WriteLine("  /exit     - Exit the agent");
    Console.WriteLine("  /clear    - Clear conversation history");
    Console.WriteLine("  /history  - Show conversation history");
    Console.WriteLine("  /mcp      - Show MCP server status");
    Console.WriteLine("  /help     - Show this help message");
    Console.ResetColor();

    while (true)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("\nYou: ");
        Console.ResetColor();
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input.StartsWith('/'))
        {
            switch (input.ToLower())
            {
                case "/exit":
                case "/quit":
                    Console.WriteLine("Goodbye!");
                    return;

                case "/clear":
                    agent.ClearConversation();
                    Console.WriteLine("Conversation history cleared.");
                    continue;

                case "/history":
                    var history = agent.GetConversationHistory();
                    if (history.Count == 0)
                    {
                        Console.WriteLine("No conversation history.");
                    }
                    else
                    {
                        Console.WriteLine("\n=== Conversation History ===");
                        foreach (var msg in history)
                        {
                            var prefix = msg.Role switch
                            {
                                "user" => "\U0001f9d1 You",
                                "assistant" => "\U0001f916 Agent",
                                "tool" => $"\U0001f527 Tool ({msg.Name})",
                                _ => $"\u2753 {msg.Role}"
                            };
                            var content = msg.Content.Length > 150
                                ? msg.Content[..150] + "..."
                                : msg.Content;
                            Console.WriteLine($"{prefix}: {content}");
                        }
                        Console.WriteLine("===========================");
                    }
                    continue;

                case "/mcp":
                    Console.WriteLine(agent.GetMcpStatus());
                    continue;

                case "/help":
                    Console.WriteLine("Commands:");
                    Console.WriteLine("  /exit     - Exit the agent");
                    Console.WriteLine("  /clear    - Clear conversation history");
                    Console.WriteLine("  /history  - Show conversation history");
                    Console.WriteLine("  /mcp      - Show MCP server status");
                    Console.WriteLine("  /help     - Show this help message");
                    continue;

                default:
                    Console.WriteLine($"Unknown command: {input}. Type /help for available commands.");
                    continue;
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Agent: ");
        Console.ResetColor();

        try
        {
            var response = await agent.ProcessMessageAsync(input);
            Console.WriteLine(response);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nError: {ex.Message}");
            Console.ResetColor();
        }
    }
}

// --- Request Model ---
public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? SessionId { get; set; }
}
