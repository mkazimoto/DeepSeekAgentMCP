namespace DeepSeekAgentMCP.Models;

/// <summary>
/// Google OAuth configuration loaded from appsettings.json.
/// </summary>
public record GoogleAuthConfig
{
    /// <summary>Google OAuth Client ID (também pode vir da env var GOOGLE_CLIENT_ID)</summary>
    public required string ClientId { get; init; }

    /// <summary>Google OAuth Client Secret (também pode vir da env var GOOGLE_CLIENT_SECRET)</summary>
    public required string ClientSecret { get; init; }

    /// <summary>Scopes to request (default: openid, profile, email)</summary>
    public List<string> Scopes { get; init; } = ["openid", "profile", "email"];

    /// <summary>Whether Google authentication is enabled</summary>
    public bool Enabled { get; init; } = false;
}
