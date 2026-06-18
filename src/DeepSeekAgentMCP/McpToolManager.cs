using System.Collections.Concurrent;
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

    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 60;
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
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();
    private Timer? _healthCheckTimer;
    private readonly int _maxConsecutiveFailures = 3;
    private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(2);
    private bool _disposed;
    private CancellationTokenSource? _shutdownCts;

    // Cached tool definitions with TTL
    private List<ToolDefinition>? _cachedToolDefinitions;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _cacheLock = new();

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
            await ConnectServerAsync(serverConfig, cancellationToken);
        }

        lock (_clientsLock)
        {
            Console.WriteLine($"[MCP] Total connected servers: {_clients.Count}");
        }

        StartHealthCheckLoop();
    }

    /// <summary>
    /// Connects to a single MCP server.
    /// </summary>
    private async Task ConnectServerAsync(McpServerConfig serverConfig, CancellationToken cancellationToken = default)
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
                    httpOptions.AdditionalHeaders = serverConfig.Headers
                        .Select(h => new KeyValuePair<string, string>(h.Key, ResolveEnvVars(h.Value)))
                        .ToDictionary();
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
                // Remove existing wrapper for the same server if reconnecting
                _clients.RemoveAll(w => string.Equals(w.ServerName, serverConfig.Name, StringComparison.Ordinal));
                _clients.Add(new McpClientWrapper(serverConfig.Name, serverConfig, client, tools));
            }

            _failureCounts.TryRemove(serverConfig.Name, out _);

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

    /// <summary>
    /// Starts periodic health check loop with automatic reconnection.
    /// </summary>
    private void StartHealthCheckLoop()
    {
        _shutdownCts = new CancellationTokenSource();
        var shutdownToken = _shutdownCts.Token;

        _healthCheckTimer = new Timer(async _ =>
        {
            if (shutdownToken.IsCancellationRequested) return;

            List<McpClientWrapper> snapshot;
            lock (_clientsLock)
            {
                snapshot = [.. _clients];
            }

            foreach (var wrapper in snapshot)
            {
                try
                {
                    using var healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await wrapper.Client.ListToolsAsync(cancellationToken: healthCts.Token);
                    _failureCounts.TryRemove(wrapper.ServerName, out int _);
                }
                catch (Exception ex)
                {
                    var count = _failureCounts.AddOrUpdate(wrapper.ServerName, 1, (_, c) => c + 1);
                    Console.WriteLine($"[MCP] Health check failed for '{wrapper.ServerName}' ({count}/{_maxConsecutiveFailures}): {ex.Message}");

                    if (count >= _maxConsecutiveFailures)
                    {
                        Console.WriteLine($"[MCP] Initiating reconnection for '{wrapper.ServerName}'...");
                        _failureCounts.TryRemove(wrapper.ServerName, out int _);

                        // Remove failed client
                        McpClient? oldClient = null;
                        McpServerConfig? serverConfig = null;
                        lock (_clientsLock)
                        {
                            var idx = _clients.FindIndex(w => string.Equals(w.ServerName, wrapper.ServerName, StringComparison.Ordinal));
                            if (idx >= 0)
                            {
                                oldClient = _clients[idx].Client;
                                serverConfig = _clients[idx].Config;
                                _clients.RemoveAt(idx);
                            }
                        }

                        // Reconnect
                        if (serverConfig != null)
                        {
                            await ConnectServerAsync(serverConfig, CancellationToken.None);
                        }

                        // Dispose old client after replacement
                        if (oldClient != null)
                        {
                            try { await oldClient.DisposeAsync(); } catch { }
                        }
                    }
                }
            }
        }, null, _healthCheckInterval, _healthCheckInterval);
    }

    /// <summary>
    /// Gets all discovered tools as DeepSeek-compatible tool definitions.
    /// Uses a cache with configurable TTL (default: 30s) to avoid recomputation.
    /// </summary>
    public virtual List<ToolDefinition> GetToolDefinitions(int cacheTtlSeconds = 30)
    {
        lock (_cacheLock)
        {
            if (_cachedToolDefinitions is not null && DateTime.UtcNow < _cacheExpiry)
                return _cachedToolDefinitions;
        }

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

        lock (_cacheLock)
        {
            _cachedToolDefinitions = definitions;
            _cacheExpiry = DateTime.UtcNow.AddSeconds(cacheTtlSeconds);
        }

        return definitions;
    }

    /// <summary>
    /// Invalidates the tool definitions cache (e.g., after reconnection).
    /// </summary>
    public void InvalidateToolCache()
    {
        lock (_cacheLock)
        {
            _cachedToolDefinitions = null;
            _cacheExpiry = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Executes an MCP tool call and returns the result as a string.
    /// </summary>
    public virtual async Task<string> ExecuteToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
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

                // Apply per-server timeout
                var timeoutSeconds = wrapper.Config.TimeoutSeconds > 0 ? wrapper.Config.TimeoutSeconds : 60;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var result = await tool.CallAsync(arguments, cancellationToken: linkedCts.Token);
                var textParts = result.Content
                    .Where(c => c is not null && c.Type == "text")
                    .Select(c => c.ToString() ?? string.Empty);

                return string.Join("\n", textParts);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return $"Error: Tool '{functionName}' timed out after {wrapper.Config.TimeoutSeconds} seconds.";
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
            var failures = _failureCounts.GetValueOrDefault(wrapper.ServerName, 0);
            var healthMark = failures > 0 ? $" ⚠ ({failures}/{_maxConsecutiveFailures} failures)" : " ✓";
            lines.Add($"  - {wrapper.ServerName}: {wrapper.Tools.Count} tool(s){healthMark}");
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
            toolCount = wrapper.Tools.Count,
            failures = _failureCounts.GetValueOrDefault(wrapper.ServerName, 0),
            timeoutSeconds = wrapper.Config.TimeoutSeconds
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
        if (_disposed) return;
        _disposed = true;

        // Signal shutdown first so the health check callback stops immediately
        if (_shutdownCts != null)
        {
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
        }

        _healthCheckTimer?.Dispose();

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

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resolves ${VAR_NAME} patterns in a string using environment variables
    /// or Windows Registry fallback (HKLM\SOFTWARE\DeepSeekAgentMCP\{VAR_NAME}).
    /// Unset variables are replaced with an empty string.
    /// </summary>
    private static string ResolveEnvVars(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${")) return value;

        return System.Text.RegularExpressions.Regex.Replace(value, @"\$\{(\w+)\}", match =>
        {
            var varName = match.Groups[1].Value;

            // 1. Try environment variable
            var envValue = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrEmpty(envValue)) return envValue;

            // 2. Fallback: Windows Registry
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        $@"SOFTWARE\DeepSeekAgentMCP");
                    if (key?.GetValue(varName) is string regValue && !string.IsNullOrEmpty(regValue))
                        return regValue;
                }
                catch
                {
                    // Ignore registry errors
                }
            }

            return string.Empty;
        });
    }

    /// <summary>
    /// Internal wrapper to associate server name, config, client, and tools.
    /// </summary>
    private record McpClientWrapper(string ServerName, McpServerConfig Config, McpClient Client, IList<McpClientTool> Tools);
}
