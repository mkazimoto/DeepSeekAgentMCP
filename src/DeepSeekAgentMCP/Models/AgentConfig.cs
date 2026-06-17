namespace DeepSeekAgentMCP.Models;

/// <summary>
/// Configuration model loaded from appsettings.json.
/// Encapsula todas as configurações do agente, eliminando parsing duplicado.
/// </summary>
public record AgentConfig
{
    /// <summary>DeepSeek API Key (pode vir do appsettings.json ou env var)</summary>
    public required string ApiKey { get; init; }

    /// <summary>Model name (default: deepseek-v4-flash)</summary>
    public required string Model { get; init; }

    /// <summary>Maximum tokens per response</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Temperature for generation</summary>
    public double Temperature { get; init; } = 0.3;

    /// <summary>Thinking configuration (optional)</summary>
    public ThinkingConfig? ThinkingConfig { get; init; }

    /// <summary>Reasoning effort (optional)</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Path to MCP servers config file</summary>
    public string McpServerConfigPath { get; init; } = "config/mcp-servers.json";

    /// <summary>Whether the web server is enabled</summary>
    public bool WebEnabled { get; init; } = true;

    /// <summary>Web server URLs</summary>
    public string WebUrls { get; init; } = "http://localhost:5000";

    /// <summary>Whether to auto-launch browser</summary>
    public bool LaunchBrowser { get; init; } = false;

    /// <summary>Rate limit: max requests per minute per IP</summary>
    public int RateLimitPerMinute { get; init; } = 30;
}
