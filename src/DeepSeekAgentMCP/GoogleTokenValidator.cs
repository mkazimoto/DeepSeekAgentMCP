using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Tokens;

namespace DeepSeekAgentMCP;

/// <summary>
/// Validates Google Identity Services (GIS) ID tokens (JWTs) on the server side.
/// Uses Google's public keys (JWKS endpoint) for cryptographic verification.
/// </summary>
internal static class GoogleTokenValidator
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string _jwksUrl = "https://www.googleapis.com/oauth2/v3/certs";

    // Cache for Google's signing keys (refreshed every hour)
    private static ICollection<SecurityKey>? _cachedKeys;
    private static DateTime _keysCacheExpiry = DateTime.MinValue;
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Validates a Google ID token and returns a ClaimsPrincipal with user info.
    /// Returns null if the token is invalid.
    /// </summary>
    public static async Task<ClaimsPrincipal?> ValidateIdTokenAsync(string idToken, string expectedClientId)
    {
        try
        {
            var keys = await GetSigningKeysAsync();
            var handler = new JwtSecurityTokenHandler();

            var result = handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidIssuer = "https://accounts.google.com",
                ValidAudience = expectedClientId,
                IssuerSigningKeys = keys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            }, out _);

            // Add login_timestamp claim for cache-busting profile pictures
            var claims = new List<Claim>(result.Claims);
            claims.Add(new Claim("login_timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches and caches Google's JWKS (JSON Web Key Set) for token validation.
    /// </summary>
    private static async Task<ICollection<SecurityKey>> GetSigningKeysAsync()
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_cachedKeys is { Count: > 0 } && DateTime.UtcNow < _keysCacheExpiry)
                return _cachedKeys;
        }

        var json = await _httpClient.GetStringAsync(_jwksUrl);

        using var doc = JsonDocument.Parse(json);
        var keys = new List<SecurityKey>();

        foreach (var key in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            var kty = key.GetProperty("kty").GetString();
            if (kty == "RSA")
            {
                var n = Base64UrlDecode(key.GetProperty("n").GetString()!);
                var e = Base64UrlDecode(key.GetProperty("e").GetString()!);
                var kid = key.TryGetProperty("kid", out var kidProp) ? kidProp.GetString() : null;

                var rsaKey = new RsaSecurityKey(new System.Security.Cryptography.RSAParameters
                {
                    Modulus = n,
                    Exponent = e
                })
                {
                    KeyId = kid
                };

                keys.Add(rsaKey);
            }
        }

        // Cache for 1 hour
        lock (_cacheLock)
        {
            _cachedKeys = keys;
            _keysCacheExpiry = DateTime.UtcNow.AddHours(1);
        }

        return keys;
    }

    /// <summary>
    /// Decodes a Base64URL-encoded string to a byte array.
    /// </summary>
    private static byte[] Base64UrlDecode(string input)
    {
        var remainder = input.Length % 4;
        string padded;
        if (remainder == 2)
            padded = input + "==";
        else if (remainder == 3)
            padded = input + "=";
        else
            padded = input;

        return Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
    }
}
