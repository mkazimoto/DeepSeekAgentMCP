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

    /// <summary>Auto-launch browser on start</summary>
    public bool LaunchBrowser { get; init; } = false;

    /// <summary>Rate limit per IP per minute</summary>
    public int RateLimitPerMinute { get; init; } = 30;

    /// <summary>Allowed CORS origins (empty = no CORS)</summary>
    public List<string> AllowedCorsOrigins { get; init; } = [];

    /// <summary>Requires authentication token for API access</summary>
    public bool RequireAuth { get; init; } = false;

    /// <summary>Authentication token for API access</summary>
    public string? AuthToken { get; init; }

    /// <summary>Whether HTTPS is enabled</summary>
    public bool HttpsEnabled { get; init; } = false;

    /// <summary>Path to HTTPS certificate file</summary>
    public string? HttpsCertificatePath { get; init; }

    /// <summary>Maximum active sessions per IP</summary>
    public int MaxSessionsPerIp { get; init; } = 5;

    /// <summary>HttpClient timeout in seconds for DeepSeek API calls</summary>
    public int HttpClientTimeoutSeconds { get; init; } = 300;

    /// <summary>Whether to continue processing remaining tool calls when one fails</summary>
    public bool ContinueOnToolError { get; init; } = true;

    /// <summary>Whether to execute independent tool calls in parallel</summary>
    public bool ParallelToolCalls { get; init; } = true;

    /// <summary>Cache TTL for MCP tool definitions in seconds</summary>
    public int ToolDefinitionsCacheSeconds { get; init; } = 30;

    /// <summary>Whether to summarize old history instead of discarding it</summary>
    public bool SummarizeHistory { get; init; } = false;

    /// <summary>Google OAuth configuration (optional)</summary>
    public GoogleAuthConfig? GoogleAuth { get; init; }

    /// <summary>Path to log DeepSeek API request/response communication (optional). Leave null/empty to disable.</summary>
    public string? ApiCommunicationLogPath { get; init; }

    /// <summary>Path to log user activity (optional). Leave null/empty to disable file logging (in-memory only).</summary>
    public string? UserLogPath { get; init; }
}
