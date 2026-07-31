namespace MyPersonalDrive.Services;

/// <summary>
/// Last-resort crash logging. Deliberately independent from <see cref="AppSettingsService"/>
/// so it keeps working even if settings loading itself is what crashed.
/// </summary>
internal static class CrashLog
{
    public static void Write(object? exceptionObject)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var baseFolder = Path.Combine(appData, "MyPersonalDrive");
            Directory.CreateDirectory(baseFolder);
            var path = Path.Combine(baseFolder, "crash.log");

            var text = exceptionObject is Exception ex ? ex.ToString() : exceptionObject?.ToString() ?? "Unknown error";
            File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // A crash handler must never throw. There is nowhere left to report this.
        }
    }
}
