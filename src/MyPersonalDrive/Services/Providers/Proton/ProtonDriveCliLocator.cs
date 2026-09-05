using MyPersonalDrive.Services;

namespace MyPersonalDrive.Services.Providers.Proton;

public sealed class ProtonDriveCliLocator : IProtonDriveCliLocator
{
    private readonly AppSettingsService _settings;

    public ProtonDriveCliLocator(AppSettingsService settings)
    {
        _settings = settings;
    }

    public string Locate()
    {
        var configuredPath = _settings.Load().CliPath;
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in EnumeratePathCandidates("proton-drive"))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("No se pudo ubicar la CLI de Proton Drive. Guardá primero la ruta del ejecutable.");
    }

    private static IEnumerable<string> EnumeratePathCandidates(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var segments = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { string.Empty };

        foreach (var segment in segments)
        {
            foreach (var extension in extensions)
            {
                yield return Path.Combine(segment, executableName + extension);
            }
        }
    }
}
