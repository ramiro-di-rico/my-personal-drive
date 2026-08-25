using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;
using MyPersonalDrive.Tests;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The listing's view mode (docs/PLAN-BROWSER-VIEWS.md V1). Two things here are worth more than
/// the rest: an unrecognized persisted value must degrade instead of throwing (the setting is a
/// string precisely so a newer version can write something this build has never heard of), and
/// persisting one setting must not wipe the others — <c>PersistSettings</c> used to build a fresh
/// <see cref="AppSettings"/>, so saving the CLI path reset everything it didn't know about.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowViewModeTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ViewMode").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-view-mode-{Guid.NewGuid():N}.db");

    public MainWindowViewModeTests()
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

    private MainWindowViewModel Build()
    {
        var service = new ProtonDriveService(new FakeCliExecutor());
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(service, store, new LocalScanner(), new RemoteScanner(service));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            service,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel);
    }

    [Fact]
    public void DefaultViewMode_WithNoSettingsFile_IsList()
    {
        var sut = Build();

        Assert.Equal(DriveViewMode.List, sut.ViewMode);
        Assert.True(sut.IsListView);
        Assert.False(sut.IsIconsView);
        Assert.False(sut.IsGalleryView);
    }

    [Fact]
    public async Task ShowGalleryView_SetsModeAndRaisesTheDerivedFlags()
    {
        var sut = Build();
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await sut.ShowGalleryViewCommand.ExecuteAsync();

        Assert.Equal(DriveViewMode.Gallery, sut.ViewMode);
        Assert.True(sut.IsGalleryView);
        Assert.False(sut.IsListView);
        Assert.Contains(nameof(MainWindowViewModel.IsListView), raised);
        Assert.Contains(nameof(MainWindowViewModel.IsIconsView), raised);
        Assert.Contains(nameof(MainWindowViewModel.IsGalleryView), raised);
    }

    [Fact]
    public async Task ViewMode_SurvivesARestart()
    {
        var first = Build();
        await first.ShowIconsViewCommand.ExecuteAsync();

        var second = Build();

        Assert.Equal(DriveViewMode.Icons, second.ViewMode);
    }

    [Fact]
    public void ViewMode_WithAnUnrecognizedPersistedValue_FallsBackToList()
    {
        var settings = new AppSettingsService();
        settings.Save(new AppSettings { ViewMode = "Hologram" });

        var sut = Build();

        Assert.Equal(DriveViewMode.List, sut.ViewMode);
    }

    [Fact]
    public async Task PersistingTheCliPath_KeepsTheViewMode()
    {
        var sut = Build();
        await sut.ShowGalleryViewCommand.ExecuteAsync();

        sut.CliPath = "/usr/bin/proton-drive";

        var reloaded = new AppSettingsService().Load();
        Assert.Equal("/usr/bin/proton-drive", reloaded.CliPath);
        Assert.Equal(DriveViewMode.Gallery, reloaded.ViewModeOrDefault());
    }

    [Fact]
    public async Task SwitchingViewMode_DoesNotDisturbTheListing()
    {
        var sut = Build();
        var item = new DriveItem("/my-files/Fotos", "Fotos", IsFolder: true);
        var node = new DriveNodeViewModel(item, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask)
        {
            IsSelected = true
        };
        sut.RootItems.Add(node);

        await sut.ShowIconsViewCommand.ExecuteAsync();

        Assert.Single(sut.RootItems);
        Assert.Same(node, sut.RootItems[0]);
        Assert.True(sut.RootItems[0].IsSelected);
    }
}
