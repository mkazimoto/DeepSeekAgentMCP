using DeepSeekAgentMCP;
using System.Text.Json;

// ============================================================
//  DeepSeek Agent with MCP (Model Context Protocol) Support
// ============================================================

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
  ____               _      ____       _            __  __  ____ ____
 |  _ \  ___  ___ __(_) ___/ ___|  ___| |__   ___  |  \/  |/ ___|  _ \
 | | | |/ _ \/ __/ _| |/ _ \___ \ / __| '_ \ / _ \ | |\/| | |   | |_) |
 | |_| |  __/ (_| |_| |  __/___) | (__| | | |  __/ | |  | | |___|  __/
 |____/ \___|\___\__|_|\___|____/ \___|_| |_|\___| |_|  |_|\____|_|
");
Console.ResetColor();
Console.WriteLine("=== DeepSeek Agent with MCP Support ===\n");

// --- Configuration ---
var configPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "appsettings.json");
configPath = Path.GetFullPath(configPath);

string apiKey;
string model;
int maxTokens;
double temperature;
if (File.Exists(configPath))
{
    var configJson = await File.ReadAllTextAsync(configPath);
    using var doc = JsonDocument.Parse(configJson);
    var deepSeekConfig = doc.RootElement.GetProperty("DeepSeek");

    apiKey = deepSeekConfig.GetProperty("ApiKey").GetString() ?? string.Empty;
    model = deepSeekConfig.GetProperty("Model").GetString() ?? "deepseek-chat";
    maxTokens = deepSeekConfig.GetProperty("MaxTokens").GetInt32();
    temperature = deepSeekConfig.GetProperty("Temperature").GetDouble();

    var mcpConfigRelPath = doc.RootElement.GetProperty("McpServerConfigPath").GetString() ?? "config/mcp-servers.json";
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

// Fallback to environment variable if config file has no API key
if (string.IsNullOrWhiteSpace(apiKey))
{
    apiKey = GetEnvDeepSeekApiKey();
}

// Check API key
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
var mcpConfigRelPath2 = "config/mcp-servers.json";
if (File.Exists(configPath))
{
    using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
    mcpConfigRelPath2 = doc2.RootElement.GetProperty("McpServerConfigPath").GetString() ?? "config/mcp-servers.json";
}
// configPath = .../config/appsettings.json -> project root is two levels up
var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? Directory.GetCurrentDirectory();
var mcpConfigFullPath = Path.GetFullPath(Path.Combine(projectRoot, mcpConfigRelPath2));

var deepSeekClient = new DeepSeekClient(apiKey, model, maxTokens, temperature);
var mcpManager = new McpToolManager(mcpConfigFullPath);
var agent = new DeepSeekAgent(deepSeekClient, mcpManager);

// Initialize MCP connections
await agent.InitializeAsync();

// Print MCP status
Console.WriteLine(agent.GetMcpStatus());
Console.WriteLine();

// --- Interactive Loop ---
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

    // Handle commands
    if (input.StartsWith('/'))
    {
        switch (input.ToLower())
        {
            case "/exit":
            case "/quit":
                Console.WriteLine("Goodbye!");
                return;

            case "/clear":
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

    // Process message
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

static string GetEnvDeepSeekApiKey()
{
    return Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User)
        ?? string.Empty;
}
