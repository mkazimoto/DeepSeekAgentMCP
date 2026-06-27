namespace DeepSeekAgentMCP.Models;

/// <summary>
/// Request payload from the client after Google Identity Services authentication.
/// Contains the ID token (JWT credential) returned by GIS.
/// </summary>
public record GoogleTokenRequest
{
    /// <summary>The Google ID token (JWT credential) returned by GIS after successful sign-in.</summary>
    public required string Credential { get; init; }

    /// <summary>Optional: the Google client ID used for this sign-in.</summary>
    public string? ClientId { get; init; }
}
