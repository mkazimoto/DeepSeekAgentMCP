using DeepSeekAgentMCP;
using System.Text.Json;
using System.Text.RegularExpressions;

// ============================================================
//  DeepSeek Agent with MCP (Model Context Protocol) Support
//  Console + Web Interface
// ============================================================

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

    apiKey = string.Empty;
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
    await RunWebServerAsync(agent, webUrls ?? "http://localhost:5000", launchBrowser);
}
else
{
    await RunConsoleModeAsync(agent);
}

// ===================================================================
//  Web Server Mode (ASP.NET Core Minimal API)
// ===================================================================
static async Task RunWebServerAsync(DeepSeekAgent agent, string urls, bool launchBrowser)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n Starting web interface at {urls}");
    Console.ResetColor();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = [],
        ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
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
            var response = await agent.ProcessMessageAsync(request.Message);
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
        var status = agent.GetMcpStatus();
        var servers = ParseMcpStatus(status);
        return Results.Ok(new
        {
            model = "deepseek-chat",
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

static List<object> ParseMcpStatus(string status)
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

// --- Request Model ---
public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
}
