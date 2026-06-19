using System.Text.Json;
using DeepSeekAgentMCP;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP.Tests;

public class McpToolManagerTests
{
    [Fact]
    public void McpServerConfig_DefaultValues_AreCorrect()
    {
        var config = new McpServerConfig();

        Assert.Equal(string.Empty, config.Name);
        Assert.True(config.Enabled);
        Assert.Equal("stdio", config.TransportType);
        Assert.Equal(string.Empty, config.Command);
        Assert.Empty(config.Arguments);
        Assert.Null(config.Url);
        Assert.Null(config.Headers);
        Assert.Null(config.EnvironmentVariables);
        Assert.Equal(60, config.TimeoutSeconds);
        Assert.Null(config.AllowedTools);
    }

    [Fact]
    public void McpServerConfig_JsonDeserialization_WithAllFields()
    {
        var json = """
            {
                "Name": "test-server",
                "Enabled": true,
                "TransportType": "http",
                "Url": "http://localhost:3000/mcp",
                "Headers": { "Authorization": "Bearer token123" },
                "TimeoutSeconds": 120
            }
            """;

        var config = JsonSerializer.Deserialize<McpServerConfig>(json);

        Assert.NotNull(config);
        Assert.Equal("test-server", config.Name);
        Assert.True(config.Enabled);
        Assert.Equal("http", config.TransportType);
        Assert.Equal("http://localhost:3000/mcp", config.Url);
        Assert.NotNull(config.Headers);
        Assert.Equal("Bearer token123", config.Headers["Authorization"]);
        Assert.Equal(120, config.TimeoutSeconds);
        Assert.Null(config.AllowedTools);
    }

    [Fact]
    public void McpServerConfig_JsonDeserialization_WithAllowedTools()
    {
        var json = """
            {
                "Name": "filtered-server",
                "TransportType": "http",
                "Url": "http://localhost:3000/mcp",
                "AllowedTools": ["consulta_*", "executa_sql"]
            }
            """;

        var config = JsonSerializer.Deserialize<McpServerConfig>(json);

        Assert.NotNull(config);
        Assert.Equal("filtered-server", config.Name);
        Assert.NotNull(config.AllowedTools);
        Assert.Equal(2, config.AllowedTools.Count);
        Assert.Equal("consulta_*", config.AllowedTools[0]);
        Assert.Equal("executa_sql", config.AllowedTools[1]);
    }

    [Fact]
    public void McpServerConfig_JsonDeserialization_WithEmptyAllowedTools()
    {
        var json = """
            {
                "Name": "filtered-server",
                "TransportType": "http",
                "Url": "http://localhost:3000/mcp",
                "AllowedTools": []
            }
            """;

        var config = JsonSerializer.Deserialize<McpServerConfig>(json);

        Assert.NotNull(config);
        Assert.NotNull(config.AllowedTools);
        Assert.Empty(config.AllowedTools);
    }

    [Fact]
    public void McpServerConfig_JsonDeserialization_StdioTransport()
    {
        var json = """
            {
                "Name": "local-server",
                "TransportType": "stdio",
                "Command": "node",
                "Arguments": ["server.js", "--port", "8080"],
                "EnvironmentVariables": { "NODE_ENV": "production" }
            }
            """;

        var config = JsonSerializer.Deserialize<McpServerConfig>(json);

        Assert.NotNull(config);
        Assert.Equal("local-server", config.Name);
        Assert.Equal("stdio", config.TransportType);
        Assert.Equal("node", config.Command);
        Assert.Equal(3, config.Arguments.Count);
        Assert.Equal("server.js", config.Arguments[0]);
        Assert.Equal("--port", config.Arguments[1]);
        Assert.Equal("8080", config.Arguments[2]);
        Assert.NotNull(config.EnvironmentVariables);
        Assert.Equal("production", config.EnvironmentVariables["NODE_ENV"]);
    }

    [Fact]
    public void McpServersConfig_Deserializes_MultipleServers()
    {
        var json = """
            {
                "McpServers": [
                    { "Name": "server-a", "TransportType": "http", "Url": "http://a:3000/mcp" },
                    { "Name": "server-b", "TransportType": "http", "Url": "http://b:3000/mcp", "Enabled": false }
                ]
            }
            """;

        var config = JsonSerializer.Deserialize<McpServersConfig>(json);

        Assert.NotNull(config);
        Assert.Equal(2, config.McpServers.Count);
        Assert.Equal("server-a", config.McpServers[0].Name);
        Assert.True(config.McpServers[0].Enabled);
        Assert.Equal("server-b", config.McpServers[1].Name);
        Assert.False(config.McpServers[1].Enabled);
    }

    [Fact]
    public void McpToolManager_Constructor_AcceptsConfigPath()
    {
        var manager = new McpToolManager("config/mcp-servers.json");
        Assert.NotNull(manager);
    }

    [Fact]
    public async Task McpToolManager_InitializeAsync_ConfigNotFound_DoesNotThrow()
    {
        var manager = new McpToolManager("nonexistent-config.json");

        // Should not throw — logs a message and returns gracefully
        var exception = await Record.ExceptionAsync(() => manager.InitializeAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task McpToolManager_GetToolDefinitions_NoServers_ReturnsEmpty()
    {
        var manager = new McpToolManager("nonexistent.json");
        await manager.InitializeAsync();

        var tools = manager.GetToolDefinitions();

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task McpToolManager_GetStatusSummary_NoServers_ReturnsMessage()
    {
        var manager = new McpToolManager("nonexistent.json");
        await manager.InitializeAsync();

        var status = manager.GetStatusSummary();

        Assert.Contains("No MCP servers connected", status);
    }

    [Fact]
    public async Task McpToolManager_GetServerStatusList_NoServers_ReturnsEmpty()
    {
        var manager = new McpToolManager("nonexistent.json");
        await manager.InitializeAsync();

        var list = manager.GetServerStatusList();

        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task McpToolManager_DisposeAsync_NoServers_DoesNotThrow()
    {
        var manager = new McpToolManager("nonexistent.json");
        await manager.InitializeAsync();

        var exception = await Record.ExceptionAsync(async () => await manager.DisposeAsync());
        Assert.Null(exception);
    }

    [Fact]
    public void ToolDefinition_Creation_HasCorrectFormat()
    {
        var toolDef = new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction
            {
                Name = "server_test_tool",
                Description = "[server] A test tool",
                Parameters = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{}}""")
            }
        };

        Assert.Equal("function", toolDef.Type);
        Assert.Equal("server_test_tool", toolDef.Function.Name);
        Assert.Contains("[server]", toolDef.Function.Description);
    }
}
