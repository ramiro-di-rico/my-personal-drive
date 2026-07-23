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

        var json = File.ReadAllText(_settingsPath);
        return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        File.WriteAllText(_settingsPath, json);
    }
}
