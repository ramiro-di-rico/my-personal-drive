using System.Text.Json;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// Persists the refresh/access token pair to <c>google-drive-token.json</c> under
/// <see cref="AppSettingsService.BaseFolder"/>, chmod 600. Exact mirror of
/// <c>OneDrive.OneDriveTokenStore</c> — same at-rest plaintext, accepted-risk shape
/// (docs/PLAN-CLOUD-PROVIDERS.md §4.2/§8.1, R3).
/// </summary>
public sealed class GoogleDriveTokenStore
{
    private readonly string _tokenPath;

    public GoogleDriveTokenStore(string baseFolder)
    {
        _tokenPath = Path.Combine(baseFolder, "google-drive-token.json");
    }

    public StoredGoogleDriveToken? Load()
    {
        if (!File.Exists(_tokenPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_tokenPath);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.StoredGoogleDriveToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable token file degrades to "signed out" rather than crashing the
            // provider — the user just has to sign in again, same as an expired refresh token.
            return null;
        }
    }

    public void Save(StoredGoogleDriveToken token)
    {
        var json = JsonSerializer.Serialize(token, AppJsonContext.Default.StoredGoogleDriveToken);
        File.WriteAllText(_tokenPath, json);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_tokenPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
