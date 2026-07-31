using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Covers docs/PLAN-TECH-DEBT.md B0.3 / crash C2: a corrupt settings.json used to throw
/// JsonException out of Load(), and since ProtonDriveCliLocator.Locate() calls Load() on every
/// CLI command, that turned every button in the app into a crash.
/// </summary>
public class AppSettingsServiceTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.AppData").FullName;
    private readonly string? _originalAppData;

    public AppSettingsServiceTests()
    {
        // On Linux, Environment.SpecialFolder.ApplicationData resolves to $XDG_CONFIG_HOME,
        // but only if that directory already exists at the time it's read - hence creating it
        // in the field initializer above, before this constructor runs.
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        Directory.Delete(_tempAppData, recursive: true);
    }

    [Fact]
    public void Load_WithNoSettingsFile_ReturnsDefaults()
    {
        var sut = new AppSettingsService();

        var settings = sut.Load();

        Assert.Equal(string.Empty, settings.CliPath);
        Assert.False(settings.IsAuthenticated);
    }

    [Fact]
    public void Load_WithCorruptJson_QuarantinesFileAndReturnsDefaults()
    {
        var sut = new AppSettingsService();
        var settingsPath = Path.Combine(sut.BaseFolder, "settings.json");
        File.WriteAllText(settingsPath, "{ this is not valid json ");

        var settings = sut.Load();

        Assert.Equal(string.Empty, settings.CliPath);
        Assert.False(File.Exists(settingsPath));
        Assert.Contains(Directory.GetFiles(sut.BaseFolder), f => f.Contains("settings.json.corrupt-"));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var sut = new AppSettingsService();
        sut.Save(new AppSettings { CliPath = "/usr/bin/proton-drive", IsAuthenticated = true, StrictListingParsing = true });

        var loaded = sut.Load();

        Assert.Equal("/usr/bin/proton-drive", loaded.CliPath);
        Assert.True(loaded.IsAuthenticated);
        Assert.True(loaded.StrictListingParsing);
    }
}
