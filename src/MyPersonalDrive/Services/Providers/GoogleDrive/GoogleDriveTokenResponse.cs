using System.Text.Json.Serialization;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// The token endpoint's response shape (`https://oauth2.googleapis.com/token`), for both the
/// initial authorization-code exchange and a refresh — same fields either way, per Google's public
/// OAuth2 docs. <see cref="ExpiresIn"/> is seconds-from-now, not an absolute time; converted to
/// <see cref="StoredGoogleDriveToken.ExpiresAt"/> at the point it's persisted. Mirrors
/// <c>OneDrive.GraphTokenResponse</c> — docs/PLAN-CLOUD-PROVIDERS.md §8.1.
/// </summary>
public sealed class GoogleDriveTokenResponse
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

/// <summary>What <see cref="GoogleDriveTokenStore"/> actually persists to disk — an absolute expiry, not a relative one.</summary>
public sealed class StoredGoogleDriveToken
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The signed-in account's display label (email/name from `drive/v3/about`), shown in the settings card.</summary>
    public string? AccountLabel { get; set; }
}
