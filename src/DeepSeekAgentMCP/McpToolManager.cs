using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeekAgentMCP.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DeepSeekAgentMCP;

/// <summary>
/// Configuration for an MCP server.
/// </summary>
public class McpServerConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("TransportType")]
    public string TransportType { get; set; } = "stdio";

    [JsonPropertyName("Command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("Arguments")]
    public List<string> Arguments { get; set; } = [];

    [JsonPropertyName("Url")]
    public string? Url { get; set; }

    [JsonPropertyName("Headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("EnvironmentVariables")]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
}

/// <summary>
/// Root configuration for MCP servers.
/// </summary>
public class McpServersConfig
{
    [JsonPropertyName("McpServers")]
    public List<McpServerConfig> McpServers { get; set; } = [];
}

/// <summary>
/// Manages MCP server connections and tool discovery.
/// </summary>
public class McpToolManager : IAsyncDisposable
{
    private readonly List<McpClientWrapper> _clients = [];
    private readonly object _clientsLock = new();
    private readonly string _configPath;

    public McpToolManager(string configPath)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Initializes all enabled MCP servers from the configuration file.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            Console.WriteLine($"[MCP] Config file not found: {_configPath}");
            return;
        }

        var json = await File.ReadAllTextAsync(_configPath, cancellationToken);
        var config = JsonSerializer.Deserialize<McpServersConfig>(json);
        if (config == null) return;

        foreach (var serverConfig in config.McpServers.Where(s => s.Enabled))
        {
            try
            {
                Console.WriteLine($"[MCP] Connecting to server: {serverConfig.Name}...");

                IClientTransport transport;

                if (serverConfig.TransportType.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    var httpOptions = new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(serverConfig.Url ?? throw new InvalidOperationException($"HTTP transport requires a 'Url' for server '{serverConfig.Name}'.")),
                        TransportMode = HttpTransportMode.StreamableHttp
                    };

                    if (serverConfig.Headers is { Count: > 0 })
                    {
                        httpOptions.AdditionalHeaders = new Dictionary<string, string>(serverConfig.Headers);
                    }

                    transport = new HttpClientTransport(httpOptions);
                }
                else
                {
                    var options = new StdioClientTransportOptions
                    {
                        Name = serverConfig.Name,
                        Command = serverConfig.Command,
                        Arguments = serverConfig.Arguments
                    };

                    transport = new StdioClientTransport(options);
                }

                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                lock (_clientsLock)
                {
                    _clients.Add(new McpClientWrapper(serverConfig.Name, client, tools));
                }

                Console.WriteLine($"[MCP] Connected to '{serverConfig.Name}' with {tools.Count} tool(s).");
                foreach (var tool in tools)
                {
                    Console.WriteLine($"  - {tool.Name}: {tool.Description}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MCP] Failed to connect to '{serverConfig.Name}': {ex.Message}");
            }
        }

        lock (_clientsLock)
        {
            Console.WriteLine($"[MCP] Total connected servers: {_clients.Count}");
        }
    }

    /// <summary>
    /// Gets all discovered tools as DeepSeek-compatible tool definitions.
    /// </summary>
    public List<ToolDefinition> GetToolDefinitions()
    {
        var definitions = new List<ToolDefinition>();
        List<McpClientWrapper> snapshot;

        lock (_clientsLock)
        {
            snapshot = [.. _clients];
        }

        foreach (var wrapper in snapshot)
        {
            foreach (var tool in wrapper.Tools)
            {
                definitions.Add(new ToolDefinition
                {
                    Function = new ToolFunction
                    {
                        Name = $"{wrapper.ServerName}_{tool.Name}",
                        Description = $"[{wrapper.ServerName}] {tool.Description}",
                        Parameters = ConvertMcpSchemaToObject(tool.JsonSchema)
                    }
                });
            }
        }

        return definitions;
    }

    /// <summary>
    /// Executes an MCP tool call and returns the result as a string.
    /// </summary>
    public async Task<string> ExecuteToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var functionName = toolCall.Function.Name;
        List<McpClientWrapper> snapshot;

        lock (_clientsLock)
        {
            snapshot = [.. _clients];
        }

        // Find the server and tool by matching {ServerName}_{ToolName}
        foreach (var wrapper in snapshot)
        {
            var prefix = $"{wrapper.ServerName}_";

            if (!functionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var actualToolName = functionName[prefix.Length..];
            var tool = wrapper.Tools.FirstOrDefault(t =>
                t.Name.Equals(actualToolName, StringComparison.OrdinalIgnoreCase));

            if (tool == null)
            {
                return $"Error: Tool '{actualToolName}' not found on server '{wrapper.ServerName}'.";
            }

            try
            {
                var arguments = string.IsNullOrWhiteSpace(toolCall.Function.Arguments)
                    ? new Dictionary<string, object?>()
                    : JsonSerializer.Deserialize<Dictionary<string, object?>>(toolCall.Function.Arguments) ?? [];

                var result = await tool.CallAsync(arguments, cancellationToken: cancellationToken);
                var textParts = result.Content
                    .Where(c => c is not null && c.Type == "text")
                    .Select(c => c.ToString() ?? string.Empty);

                return string.Join("\n", textParts);
            }
            catch (Exception ex)
            {
                return $"Error calling tool '{functionName}': {ex.Message}";
            }
        }

        return $"Error: No server found for tool '{functionName}'.";
    }

    /// <summary>
    /// Gets a summary of connected MCP servers and tools.
    /// </summary>
    public string GetStatusSummary()
    {
        List<McpClientWrapper> snapshot;

        lock (_clientsLock)
        {
            snapshot = [.. _clients];
        }

        if (snapshot.Count == 0) return "No MCP servers connected.";

        var lines = new List<string> { $"MCP Servers ({snapshot.Count} connected):" };
        foreach (var wrapper in snapshot)
        {
            lines.Add($"  - {wrapper.ServerName}: {wrapper.Tools.Count} tool(s)");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Returns structured server status data for the web API.
    /// </summary>
    public List<object> GetServerStatusList()
    {
        List<McpClientWrapper> snapshot;

        lock (_clientsLock)
        {
            snapshot = [.. _clients];
        }

        return snapshot.Select(wrapper => (object)new
        {
            name = wrapper.ServerName,
            connected = true,
            toolCount = wrapper.Tools.Count
        }).ToList();
    }

    /// <summary>
    /// Converts an MCP JSON schema to a plain object for the DeepSeek API.
    /// </summary>
    private static object ConvertMcpSchemaToObject(JsonElement? schema)
    {
        if (schema == null || schema.Value.ValueKind != JsonValueKind.Object)
            return new { };

        // Return the schema as-is since DeepSeek accepts standard JSON Schema
        return schema.Value;
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClientWrapper> snapshot;

        lock (_clientsLock)
        {
            snapshot = [.. _clients];
            _clients.Clear();
        }

        foreach (var wrapper in snapshot)
        {
            try
            {
                await wrapper.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MCP] Error disposing '{wrapper.ServerName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Internal wrapper to associate server name with client and tools.
    /// </summary>
    private record McpClientWrapper(string ServerName, McpClient Client, IList<McpClientTool> Tools);
}
