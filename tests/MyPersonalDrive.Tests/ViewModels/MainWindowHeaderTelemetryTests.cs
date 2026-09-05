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

    // Asserts on ConnectionStatusKind, not ConnectionStatus: the kind is the stable token the view
    // binds its classes to, while the status text is user-facing copy that U4 translated. Testing
    // the copy made these tests fail on a pure wording change (docs/PLAN-UX-ROUND-2.md §2).
    [Fact]
    public void ConnectionTelemetry_ReflectsOnlineAndDisconnectedStates()
    {
        var sutAuthenticated = Build(isAuthenticated: true);
        Assert.Equal("Online", sutAuthenticated.ConnectionStatusKind);
        Assert.True(sutAuthenticated.IsOnline);
        Assert.False(sutAuthenticated.IsDisconnected);
        Assert.False(sutAuthenticated.IsConnectionActionable);

        var sutDisconnected = Build(isAuthenticated: false);
        Assert.Equal("Disconnected", sutDisconnected.ConnectionStatusKind);
        Assert.True(sutDisconnected.IsDisconnected);
        Assert.False(sutDisconnected.IsOnline);
        Assert.True(sutDisconnected.IsConnectionActionable);
    }

    [Fact]
    public void ConnectionTelemetry_ReflectsRateLimitedWarning()
    {
        var sut = Build(isAuthenticated: true);
        Assert.Equal("Online", sut.ConnectionStatusKind);

        sut.StatusMessage = "Rate limit exceeded (HTTP 429). Please wait.";
        // Setting StatusMessage cleared IsWarning; telemetry classifies off the typed DriveErrorKind
        // a real DriveException(Kind: RateLimited) would have left in _lastErrorKind, not off this
        // message text, so simulate both directly.
        SetErrorKind(sut, DriveErrorKind.RateLimited);
        SetWarning(sut);

        Assert.Equal("RateLimited", sut.ConnectionStatusKind);
        Assert.True(sut.IsRateLimited);
    }

    // U2: the header used to keep saying "Online" while the body reported a failed load, because
    // only RateLimited had a branch. Any failure of the connection itself now demotes the badge.
    [Theory]
    [InlineData(DriveErrorKind.Network)]
    [InlineData(DriveErrorKind.Timeout)]
    [InlineData(DriveErrorKind.NotAuthenticated)]
    [InlineData(DriveErrorKind.PermissionDenied)]
    [InlineData(DriveErrorKind.Busy)]
    public void ConnectionTelemetry_DemotesToDegraded_WhenAConnectionFailureIsStanding(DriveErrorKind kind)
    {
        var sut = Build(isAuthenticated: true);
        Assert.Equal("Online", sut.ConnectionStatusKind);

        sut.StatusMessage = "Failed to load /my-files: Invalid access token";
        SetErrorKind(sut, kind);
        SetWarning(sut);

        Assert.Equal("Degraded", sut.ConnectionStatusKind);
        Assert.True(sut.IsDegraded);
        Assert.False(sut.IsOnline);
        Assert.True(sut.IsConnectionActionable);
    }

    // A path that no longer exists says nothing about the connection — the badge must stay Online,
    // or every stale bookmark would look like an outage.
    [Fact]
    public void ConnectionTelemetry_StaysOnline_WhenTheFailureIsAboutOnePath()
    {
        var sut = Build(isAuthenticated: true);

        sut.StatusMessage = "Warning: The path '/my-files/gone' no longer exists.";
        SetErrorKind(sut, DriveErrorKind.NotFound);
        SetWarning(sut);

        Assert.Equal("Online", sut.ConnectionStatusKind);
        Assert.False(sut.IsConnectionActionable);
    }

    // U1: a warning the user cannot act on is a dead end. An expired session offers sign-in;
    // anything else offers a retry.
    [Fact]
    public void StatusAction_OffersReconnect_WhenTheSessionExpired()
    {
        var sut = Build(isAuthenticated: true);
        Assert.False(sut.HasStatusAction);

        sut.StatusMessage = "Failed to load /my-files: Invalid access token";
        SetErrorKind(sut, DriveErrorKind.NotAuthenticated);
        SetWarning(sut);

        Assert.True(sut.HasStatusAction);
        Assert.Equal("Reconectar", sut.StatusActionLabel);
        Assert.Same(sut.AuthenticateCommand, sut.StatusActionCommand);
    }

    [Fact]
    public void StatusAction_OffersRetry_ForATransientFailure()
    {
        var sut = Build(isAuthenticated: true);

        sut.StatusMessage = "Failed to load /my-files: connection reset";
        SetErrorKind(sut, DriveErrorKind.Network);
        SetWarning(sut);

        Assert.True(sut.HasStatusAction);
        Assert.Equal("Reintentar", sut.StatusActionLabel);
        Assert.Same(sut.RefreshCommand, sut.StatusActionCommand);
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
        Assert.True(sut.IsQuotaUsageKnown);
        Assert.Contains("300", sut.QuotaDisplay);
        Assert.Contains("500", sut.QuotaDisplay);

        // A folder is present, so the sum covers the root's own files only — a lower bound, and
        // labelled as one rather than dressed up with a percentage (U3).
        Assert.StartsWith("≥", sut.QuotaDisplay);
        Assert.DoesNotContain("%", sut.QuotaDisplay);
    }

    // The bug U3 fixes: files whose size the provider never reported summed to 0, and the header
    // announced "0 B / 500 GB (0% used)" above a folder full of them.
    [Fact]
    public void StorageQuota_ReportsUnknown_WhenNoFileHasASize()
    {
        var sut = Build();

        sut.DisplayItems(new List<DriveItem>
        {
            new DriveItem("Notas.gdoc", "/my-files/Notas.gdoc", false, null),
            new DriveItem("Plan.gsheet", "/my-files/Plan.gsheet", false, null)
        });

        Assert.False(sut.IsQuotaUsageKnown);
        Assert.Equal(0.0, sut.QuotaPercent);
        Assert.StartsWith("—", sut.QuotaDisplay);
        Assert.DoesNotContain("0 B", sut.QuotaDisplay);
    }

    // ...but a genuinely empty root is a real zero, and must not be hidden behind the em dash.
    [Fact]
    public void StorageQuota_ReportsAnExactZero_WhenTheRootIsActuallyEmpty()
    {
        var sut = Build();

        sut.DisplayItems(new List<DriveItem>());

        Assert.True(sut.IsQuotaUsageKnown);
        Assert.Equal(0, sut.QuotaUsedBytes);
        Assert.Contains("0 B", sut.QuotaDisplay);
        Assert.Contains("%", sut.QuotaDisplay);
    }

    private static void SetErrorKind(MainWindowViewModel sut, DriveErrorKind kind)
        => typeof(MainWindowViewModel)
            .GetField("_lastErrorKind", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(sut, kind);

    private static void SetWarning(MainWindowViewModel sut)
    {
        typeof(MainWindowViewModel)
            .GetProperty("IsWarning", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(sut, true);
        sut.UpdateConnectionTelemetry();
    }

    // U9 (docs/PLAN-UX-ROUND-2.md §9): the search box hid rows without saying how many it hid, and
    // the only way back was selecting the text and deleting it.
    [Fact]
    public async Task Search_ReportsItsResultCount_AndCanBeCleared()
    {
        var sut = Build();
        sut.DisplayItems(new List<DriveItem>
        {
            new DriveItem("informe.pdf", "/my-files/informe.pdf", false, 10),
            new DriveItem("informe-final.pdf", "/my-files/informe-final.pdf", false, 20),
            new DriveItem("fotos", "/my-files/fotos", true, null),
        });

        // Nothing typed: no count label and nothing to clear, so neither costs any space.
        Assert.False(sut.HasSearchText);
        Assert.Equal(string.Empty, sut.SearchResultText);
        Assert.False(sut.ClearSearchCommand.CanExecute(null));

        sut.SearchText = "informe";
        Assert.True(sut.HasSearchText);
        Assert.Equal("2 resultados", sut.SearchResultText);
        Assert.True(sut.ClearSearchCommand.CanExecute(null));

        sut.SearchText = "informe-final";
        Assert.Equal("1 resultado", sut.SearchResultText);

        await sut.ClearSearchCommand.ExecuteAsync();
        Assert.Equal(string.Empty, sut.SearchText);
        Assert.False(sut.HasSearchText);
        Assert.Equal(3, sut.RootItems.Count);
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

    // U5: three views, one value. The pair of booleans this replaced could represent "both" and
    // "neither", and neither is a screen (docs/PLAN-UX-ROUND-2.md §5).
    [Fact]
    public async Task TopLevelViews_AreMutuallyExclusive()
    {
        var sut = Build();

        Assert.Equal(MainView.Explorer, sut.ActiveView);
        AssertExactlyOneViewIsActive(sut);

        await sut.ShowSyncCommand.ExecuteAsync();
        Assert.Equal(MainView.Sync, sut.ActiveView);
        Assert.True(sut.IsSyncView);
        AssertExactlyOneViewIsActive(sut);

        await sut.ShowSettingsCommand.ExecuteAsync();
        Assert.Equal(MainView.Settings, sut.ActiveView);
        AssertExactlyOneViewIsActive(sut);

        await sut.ShowExplorerCommand.ExecuteAsync();
        Assert.Equal(MainView.Explorer, sut.ActiveView);
        AssertExactlyOneViewIsActive(sut);
    }

    // The Ctrl+, toggle predates the sync view; leaving sync must land on the explorer, not
    // silently keep the user where they were.
    [Fact]
    public async Task SettingsShortcut_FromTheSyncView_OpensSettings_ThenReturnsToTheExplorer()
    {
        var sut = Build();
        await sut.ShowSyncCommand.ExecuteAsync();

        await sut.ToggleSettingsCommand.ExecuteAsync();
        Assert.True(sut.IsSettingsView);

        await sut.ToggleSettingsCommand.ExecuteAsync();
        Assert.True(sut.IsExplorerView);
    }

    private static void AssertExactlyOneViewIsActive(MainWindowViewModel sut)
        => Assert.Equal(1, new[] { sut.IsExplorerView, sut.IsSyncView, sut.IsSettingsView }.Count(active => active));

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

    // U8 (docs/PLAN-UX-ROUND-2.md §8): the Conexión tabs used to bind only Is*Active, so a provider
    // that had never been configured was indistinguishable from a signed-in one.
    [Fact]
    public void ProviderTabs_ExposeAuthState_SeparatelyFromWhichTabIsSelected()
    {
        var sut = Build(isAuthenticated: true);

        // Proton is both the active provider and the authenticated one...
        Assert.True(sut.IsProtonActive);
        Assert.True(sut.IsProtonAuthenticated);

        // ...while the rest are neither: selection and session are different axes.
        Assert.False(sut.IsOneDriveActive);
        Assert.False(sut.IsOneDriveAuthenticated);
        Assert.False(sut.IsGoogleDriveAuthenticated);
        Assert.False(sut.IsNextcloudAuthenticated);
        Assert.False(sut.IsS3Authenticated);
    }

    [Fact]
    public void ProviderTabs_ReadTheActiveProvidersLiveAuthState_NotOnlyThePersistedFlag()
    {
        var sut = Build(isAuthenticated: false);

        Assert.True(sut.IsProtonActive);
        Assert.False(sut.IsProtonAuthenticated);
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

    [Fact]
    public void ViewerZoom_DefaultsToFiftyPercent_AndPersistsChanges()
    {
        var sut = Build();
        Assert.Equal(0.5, sut.ViewerZoom);

        sut.ViewerZoom = 1.0;

        Assert.Equal(1.0, sut.ViewerZoom);
        Assert.Equal(1.0, new AppSettingsService().Load().ViewerZoom);
    }

    [Theory]
    [InlineData(0.01, AppSettings.MinViewerZoom)]
    [InlineData(10.0, AppSettings.MaxViewerZoom)]
    public void ViewerZoom_ClampsOutOfRangeValues(double attempted, double expected)
    {
        var sut = Build();

        sut.ViewerZoom = attempted;

        Assert.Equal(expected, sut.ViewerZoom);
        Assert.Equal(expected, new AppSettingsService().Load().ViewerZoom);
    }

    [Fact]
    public async Task PanelVisibilityToggles_FlipStateAndPersist_AndRestoreOnNextLaunch()
    {
        var sut = Build();
        Assert.True(sut.IsStatusPanelVisible);
        Assert.True(sut.IsLocalExplorerPanelVisible);

        // Status: a plain two-way-bound "User Settings" checkbox, set directly like DefaultSyncFolder.
        sut.IsStatusPanelVisible = false;
        // Local explorer: still a header toggle button, backed by a command.
        await sut.ToggleLocalExplorerPanelCommand.ExecuteAsync();

        Assert.False(sut.IsStatusPanelVisible);
        Assert.False(sut.IsLocalExplorerPanelVisible);

        var settings = new AppSettingsService().Load();
        Assert.False(settings.ShowStatusPanel);
        Assert.False(settings.ShowLocalExplorerPanel);

        // A fresh instance reads back the collapsed state left by the one above — the persisted
        // value is also next launch's default, not just this session's runtime toggle.
        var relaunched = Build();
        Assert.False(relaunched.IsStatusPanelVisible);
        Assert.False(relaunched.IsLocalExplorerPanelVisible);
    }
}
