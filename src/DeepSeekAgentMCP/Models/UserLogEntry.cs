using System.Text.Json.Serialization;

namespace DeepSeekAgentMCP.Models;

/// <summary>
/// Represents a single user activity log entry.
/// </summary>
public record UserLogEntry
{
    /// <summary>UTC timestamp of the event.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Event type (login, logout, message_sent, session_created, etc.).</summary>
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    /// <summary>User identifier (email, name, or "anonymous").</summary>
    [JsonPropertyName("user")]
    public string User { get; init; } = "anonymous";

    /// <summary>User email (if available).</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>Session ID.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>Client IP address.</summary>
    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    /// <summary>Additional details about the event.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// Known event type constants for user logging.
/// </summary>
public static class UserLogEvents
{
    public const string Login = "login";
    public const string Logout = "logout";
    public const string MessageSent = "message_sent";
    public const string SessionCreated = "session_created";
    public const string SessionRemoved = "session_removed";
    public const string SessionCleaned = "session_cleaned";
    public const string SessionCleared = "session_cleared";
    public const string RequestCancelled = "request_cancelled";
    public const string RateLimitExceeded = "rate_limit_exceeded";
    public const string SessionLimitExceeded = "session_limit_exceeded";
    public const string Error = "error";
}
