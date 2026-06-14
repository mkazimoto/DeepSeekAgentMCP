using DeepSeekAgentMCP;
using System.Text.Json;
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
    await RunAsConsoleAsync();
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
static async Task RunAsConsoleAsync()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(@"DeepSeek Agent with MCP Support");
    Console.ResetColor();
    Console.WriteLine("=== DeepSeek Agent with MCP Support ===\n");

    // --- Configuration ---
    var configPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "appsettings.json");
    configPath = Path.GetFullPath(configPath);

    string apiKey;
    string model;
    int maxTokens;
    double temperature;
    bool webEnabled = false;
    string? webUrls = null;
    bool launchBrowser = false;

    if (File.Exists(configPath))
    {
        var configJson = await File.ReadAllTextAsync(configPath);
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
            webEnabled = webConfig.GetProperty("Enabled").GetBoolean();
            webUrls = webConfig.GetProperty("Urls").GetString();
            launchBrowser = webConfig.GetProperty("LaunchBrowser").GetBoolean();
        }

        Console.WriteLine($"Config file: {configPath}");
    }
    else
    {
        Console.WriteLine("Config file not found. Using environment variables or defaults.");
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
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n\u26a0 DeepSeek API Key not found!");
        Console.WriteLine("Please set it in one of the following ways:");
        Console.WriteLine("  1. Edit config/appsettings.json and add your ApiKey");
        Console.WriteLine("  2. Set environment variable: DEEPSEEK_API_KEY");
        Console.ResetColor();
        Console.Write("\nEnter your DeepSeek API Key: ");
        apiKey = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No API key provided. Exiting.");
            Console.ResetColor();
            return;
        }
    }

    // --- Initialize Agent ---
    var mcpConfigRelPath = "config/mcp-servers.json";
    if (File.Exists(configPath))
    {
        using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        mcpConfigRelPath = doc2.RootElement.GetProperty("McpServerConfigPath").GetString() ?? "config/mcp-servers.json";
    }

    var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? Directory.GetCurrentDirectory();
    var mcpConfigFullPath = Path.GetFullPath(Path.Combine(projectRoot, mcpConfigRelPath));

    var deepSeekClient = new DeepSeekClient(apiKey, model, maxTokens, temperature);
    var mcpManager = new McpToolManager(mcpConfigFullPath);
    var agent = new DeepSeekAgent(deepSeekClient, mcpManager);

    await agent.InitializeAsync();

    Console.WriteLine(agent.GetMcpStatus());
    Console.WriteLine();

    // --- Start Web Server or Console Mode ---
    if (webEnabled)
    {
        var sessionManager = new SessionManager(deepSeekClient, mcpManager);
        await RunWebServerAsync(sessionManager, mcpManager, webUrls ?? "http://localhost:5000", launchBrowser);
    }
    else
    {
        await RunConsoleModeAsync(agent);
    }
}

// ===================================================================
//  Web Server Mode (ASP.NET Core Minimal API)
// ===================================================================
static async Task RunWebServerAsync(SessionManager sessionManager, McpToolManager mcpManager, string urls, bool launchBrowser)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n Starting web interface at {urls}");
    Console.ResetColor();

    // Content root is the base directory (where config/ and wwwroot/ live)
    var contentRoot = Path.GetFullPath(AppContext.BaseDirectory);
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

    builder.WebHost.UseUrls(urls);
    var app = builder.Build();
    app.UseStaticFiles();

    // POST /api/chat — send a message
    app.MapPost("/api/chat", async (ChatRequest request) =>
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = "Message is required." });

        try
        {
            var sessionId = GetSessionId(request);
            var response = await sessionManager.ProcessMessageAsync(sessionId, request.Message);
            return Results.Ok(new { response });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
    });

    // GET /api/status — MCP server status
    app.MapGet("/api/status", () =>
    {
        return Results.Ok(new
        {
            model = "deepseek-chat",
            activeSessions = sessionManager.ActiveSessionCount,
            mcpServers = mcpManager.GetServerStatusList()
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
        var sessionId = GetSessionId(request);
        sessionManager.CancelRequest(sessionId);
        return Results.Ok(new { success = true });
    });

    // POST /api/clear — clear conversation for a session
    app.MapPost("/api/clear", (ChatRequest request) =>
    {
        var sessionId = GetSessionId(request);
        sessionManager.ClearConversation(sessionId);
        return Results.Ok(new { success = true });
    });

    app.MapFallbackToFile("index.html");

    if (launchBrowser)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = urls,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    await app.RunAsync();
}

static string GetSessionId(ChatRequest request)
{
    return !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : "default";
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

// ===================================================================
//  Helper Methods
// ===================================================================
static string GetEnvDeepSeekApiKey()
{
    return Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User)
        ?? string.Empty;
}

// --- Request Model ---
public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? SessionId { get; set; }
}
