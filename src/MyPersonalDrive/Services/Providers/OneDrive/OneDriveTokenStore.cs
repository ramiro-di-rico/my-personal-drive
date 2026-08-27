using System.Text.Json;
using MyPersonalDrive.Services;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Persists the refresh/access token pair to <c>onedrive-token.json</c> under
/// <see cref="AppSettingsService.BaseFolder"/>, chmod 600. This is at-rest plaintext — accepted for
/// the first version (docs/PLAN-CLOUD-PROVIDERS.md §4.2, R3): the alternatives are a libsecret
/// P/Invoke (a native dependency and an AOT/packaging cost) or DPAPI (Windows-only, and this app
/// targets Linux). Same risk category as where the Proton CLI keeps its own session.
/// </summary>
public sealed class OneDriveTokenStore
{
    private readonly string _tokenPath;

    public OneDriveTokenStore(string baseFolder)
    {
        _tokenPath = Path.Combine(baseFolder, "onedrive-token.json");
    }

    public StoredOneDriveToken? Load()
    {
        if (!File.Exists(_tokenPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_tokenPath);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.StoredOneDriveToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable token file degrades to "signed out" rather than crashing the
            // provider — the user just has to sign in again, same as an expired refresh token.
            return null;
        }
    }

    public void Save(StoredOneDriveToken token)
    {
        var json = JsonSerializer.Serialize(token, AppJsonContext.Default.StoredOneDriveToken);
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
