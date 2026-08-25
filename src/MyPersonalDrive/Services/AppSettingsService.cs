using System.Text.Json;

namespace MyPersonalDrive.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        BaseFolder = Path.Combine(appData, "MyPersonalDrive");
        Directory.CreateDirectory(BaseFolder);
        _settingsPath = Path.Combine(BaseFolder, "settings.json");
    }

    public string BaseFolder { get; }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineCorruptFile();
            return new AppSettings();
        }
    }

    /// <summary>
    /// Read-modify-write of the whole settings file. Callers that only know about one field must
    /// use this instead of <see cref="Save"/> with a fresh <see cref="AppSettings"/>: constructing
    /// a new instance silently resets every field the caller doesn't know about, so persisting the
    /// CLI path used to wipe the view mode.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        var settings = Load();
        mutate(settings);
        Save(settings);
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence: losing the settings write should not crash the app.
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var quarantinePath = $"{_settingsPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(_settingsPath, quarantinePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we can't even move it aside, fall through with in-memory defaults.
        }
    }
}
