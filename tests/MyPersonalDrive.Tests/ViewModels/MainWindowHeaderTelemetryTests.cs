using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;
using MyPersonalDrive.Tests;

namespace MyPersonalDrive.Tests.ViewModels;

[Collection(AppDataCollection.Name)]
public class MainWindowHeaderTelemetryTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.HeaderTelemetry").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-telemetry-{Guid.NewGuid():N}.db");

    public MainWindowHeaderTelemetryTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private MainWindowViewModel Build(bool? isAuthenticated = null)
    {
        var settingsService = new AppSettingsService();
        if (isAuthenticated.HasValue)
        {
            settingsService.Update(s => s.IsAuthenticated = isAuthenticated.Value);
        }
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            settingsService,
            panel);
    }

    [Fact]
    public async Task Theme_SwitchingAndCycling_UpdatesStateAndPersists()
    {
        var sut = Build();

        Assert.Equal("Default", sut.ThemePreference);
        Assert.True(sut.IsSystemTheme);
        Assert.False(sut.IsLightTheme);
        Assert.False(sut.IsDarkTheme);

        await sut.SetThemeLightCommand.ExecuteAsync();
        Assert.Equal("Light", sut.ThemePreference);
        Assert.True(sut.IsLightTheme);
        Assert.False(sut.IsSystemTheme);
        Assert.False(sut.IsDarkTheme);

        await sut.SetThemeDarkCommand.ExecuteAsync();
        Assert.Equal("Dark", sut.ThemePreference);
        Assert.True(sut.IsDarkTheme);
        Assert.False(sut.IsLightTheme);

        // Cycle theme Dark -> Default -> Light -> Dark
        await sut.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal("Default", sut.ThemePreference);

        await sut.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal("Light", sut.ThemePreference);

        // Restarting reproduces the saved preference
        var second = Build();
        Assert.Equal("Light", second.ThemePreference);
    }

    [Fact]
    public void Theme_WithUnrecognizedValue_FallsBackToDefault()
    {
        var settingsService = new AppSettingsService();
        settingsService.Save(new AppSettings { Theme = "NeonPink" });

        var sut = Build();
        Assert.Equal("Default", sut.ThemePreference);
        Assert.True(sut.IsSystemTheme);
    }

    [Fact]
    public void ConnectionTelemetry_ReflectsOnlineAndDisconnectedStates()
    {
        var sutAuthenticated = Build(isAuthenticated: true);
        Assert.Equal("Online", sutAuthenticated.ConnectionStatus);
        Assert.True(sutAuthenticated.IsOnline);
        Assert.False(sutAuthenticated.IsDisconnected);

        var sutDisconnected = Build(isAuthenticated: false);
        Assert.Equal("Disconnected", sutDisconnected.ConnectionStatus);
        Assert.True(sutDisconnected.IsDisconnected);
        Assert.False(sutDisconnected.IsOnline);
    }

    [Fact]
    public void ConnectionTelemetry_ReflectsRateLimitedWarning()
    {
        var sut = Build(isAuthenticated: true);
        Assert.Equal("Online", sut.ConnectionStatus);

        sut.StatusMessage = "Rate limit exceeded (HTTP 429). Please wait.";
        // Setting StatusMessage cleared IsWarning; telemetry classifies off the typed DriveErrorKind
        // a real DriveException(Kind: RateLimited) would have left in _lastErrorKind, not off this
        // message text, so simulate both directly.
        typeof(MainWindowViewModel).GetField("_lastErrorKind", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(sut, DriveErrorKind.RateLimited);
        typeof(MainWindowViewModel).GetProperty("IsWarning", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(sut, true);
        sut.UpdateConnectionTelemetry();

        Assert.Equal("Rate-Limited", sut.ConnectionStatus);
        Assert.True(sut.IsRateLimited);
    }

    [Fact]
    public void StorageQuota_CalculatesMetricsFromLoadedItems()
    {
        var sut = Build();

        var items = new List<DriveItem>
        {
            new DriveItem("file1.pdf", "/my-files/file1.pdf", false, 1024 * 1024 * 100), // 100 MB
            new DriveItem("file2.zip", "/my-files/file2.zip", false, 1024 * 1024 * 200), // 200 MB
            new DriveItem("Docs", "/my-files/Docs", true, null)
        };

        sut.DisplayItems(items);

        Assert.Equal(1024 * 1024 * 300, sut.QuotaUsedBytes); // 300 MB
        Assert.Equal(500L * 1024 * 1024 * 1024, sut.QuotaTotalBytes); // 500 GB
        Assert.True(sut.QuotaPercent > 0.0);
        Assert.Contains("300", sut.QuotaDisplay);
        Assert.Contains("500", sut.QuotaDisplay);
    }

    [Fact]
    public async Task SettingsShortcut_TogglesBetweenExplorerAndSettings()
    {
        var sut = Build();

        Assert.False(sut.IsSettingsView);

        await sut.ToggleSettingsCommand.ExecuteAsync();
        Assert.True(sut.IsSettingsView);

        await sut.ToggleSettingsCommand.ExecuteAsync();
        Assert.False(sut.IsSettingsView);
    }

    [Fact]
    public void SettingsShortcut_KeyGesturesAreValid()
    {
        var ctrlGesture = Avalonia.Input.KeyGesture.Parse("Ctrl+OemComma");
        var cmdGesture = Avalonia.Input.KeyGesture.Parse("Cmd+OemComma");

        Assert.Equal(Avalonia.Input.Key.OemComma, ctrlGesture.Key);
        Assert.Equal(Avalonia.Input.KeyModifiers.Control, ctrlGesture.KeyModifiers);

        Assert.Equal(Avalonia.Input.Key.OemComma, cmdGesture.Key);
        Assert.Equal(Avalonia.Input.KeyModifiers.Meta, cmdGesture.KeyModifiers);
    }

    [Fact]
    public void SelectedProvider_ReflectsActiveProvider_AndListsAvailable()
    {
        var sut = Build();

        Assert.NotNull(sut.SelectedProvider);
        Assert.Equal(ProviderId.Proton, sut.SelectedProvider!.Id);
        Assert.Equal("Proton Drive", sut.SelectedProvider.DisplayName);
        Assert.NotEmpty(sut.AvailableProviders);
    }

    [Fact]
    public void BandwidthLimitAndDefaultSyncFolder_PersistToAppSettings()
    {
        var sut = Build();

        sut.BandwidthLimitKbps = 5120;
        sut.DefaultSyncFolder = "/home/user/DriveSync";

        var settings = new AppSettingsService().Load();
        Assert.Equal(5120, settings.BandwidthLimitKbps);
        Assert.Equal("/home/user/DriveSync", settings.DefaultSyncFolder);
    }
}
