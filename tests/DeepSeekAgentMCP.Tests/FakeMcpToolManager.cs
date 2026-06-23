using DeepSeekAgentMCP;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP.Tests;

/// <summary>
/// Fake McpToolManager for testing — returns pre-configured tool definitions and results.
/// Tracks which tools were called.
/// </summary>
public class FakeMcpToolManager : McpToolManager
{
    private List<ToolDefinition> _toolDefinitions = [];
    private readonly Dictionary<string, string> _toolResults = new(StringComparer.Ordinal);
    public List<ToolCall> ExecutedToolCalls { get; } = [];

    public FakeMcpToolManager() : base("fake-config.json") { }

    public bool SimulateConnected { get; set; } = true;

    public override bool IsAnyServerConnected => SimulateConnected;

    public void SetToolDefinitions(List<ToolDefinition> definitions)
    {
        _toolDefinitions = definitions;
    }

    public void SetToolResult(string toolName, string result)
    {
        _toolResults[toolName] = result;
    }

    public override List<ToolDefinition> GetToolDefinitions(int cacheTtlSeconds = 30)
    {
        return _toolDefinitions;
    }

    public override async Task<string> ExecuteToolCallAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutedToolCalls.Add(toolCall);
        return _toolResults.TryGetValue(toolCall.Function.Name, out var result)
            ? result
            : $"Executed: {toolCall.Function.Name}({toolCall.Function.Arguments})";
    }

    public new string GetStatusSummary()
    {
        return _toolDefinitions.Count > 0
            ? $"Fake MCP Servers (1 connected):\n  - fake-server: {_toolDefinitions.Count} tool(s)"
            : "No MCP servers connected.";
    }
}
