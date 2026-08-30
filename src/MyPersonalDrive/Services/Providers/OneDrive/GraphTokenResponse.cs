using System.Text.Json.Serialization;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// The token endpoint's response shape (`/common/oauth2/v2.0/token`), for both the initial
/// authorization-code exchange and a refresh — same fields either way, per Microsoft's public
/// OAuth2 docs. <see cref="ExpiresIn"/> is seconds-from-now, not an absolute time; converted to
/// <see cref="StoredOneDriveToken.ExpiresAt"/> at the point it's persisted.
/// </summary>
public sealed class GraphTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>What <see cref="OneDriveTokenStore"/> actually persists to disk — an absolute expiry, not a relative one.</summary>
public sealed class StoredOneDriveToken
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The signed-in account's display label (email or name from the `/me` call), shown in the settings card.</summary>
    public string? AccountLabel { get; set; }
}
