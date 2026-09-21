using Microsoft.Data.Sqlite;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The compact strip (docs/PLAN-COMPACT-VIEW.md). Resizing, pinning and placing the window is the
/// view's half and needs a visual tree; what belongs here is the half the view model owns — the
/// mode itself, the geometry it hands back to the view, and the fact that all three survive a
/// restart, which is the whole point of the feature for anyone who parks the app as a strip.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowCompactModeTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Compact").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-compact-{Guid.NewGuid():N}.db");

    public MainWindowCompactModeTests()
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
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        var syncStore = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, syncStore, new LocalScanner(), new RemoteScanner(provider));

        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            new SyncPanelViewModel(syncStore, syncExecutor, new SyncCrashRecovery(syncStore)));
    }

    [Fact]
    public async Task TheWindowStartsExpanded_AndTheToggleCollapsesAndExpandsIt()
    {
        var viewModel = Build();
        Assert.False(viewModel.IsCompactMode);

        await viewModel.ToggleCompactModeCommand.ExecuteAsync();
        Assert.True(viewModel.IsCompactMode);

        await viewModel.ToggleCompactModeCommand.ExecuteAsync();
        Assert.False(viewModel.IsCompactMode);
    }

    [Fact]
    public async Task ClosingWhileCompact_ReopensCompact()
    {
        var viewModel = Build();
        await viewModel.ToggleCompactModeCommand.ExecuteAsync();

        Assert.True(Build().IsCompactMode);
    }

    [Fact]
    public async Task TheModeChange_NotifiesSoTheLayoutCanFollowIt()
    {
        var viewModel = Build();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await viewModel.ToggleCompactModeCommand.ExecuteAsync();

        // Every pane binds IsVisible to the negation of this, and the view resizes the window off
        // the same notification — without it the strip would be a settings entry and nothing else.
        Assert.Contains(nameof(MainWindowViewModel.IsCompactMode), raised);
    }

    [Fact]
    public void WithNothingRecordedYet_TheViewIsToldToKeepItsOwnDefaults()
    {
        var viewModel = Build();

        // Null rather than a made-up 1280x800 or 0,0: the view's XAML defaults and the window
        // manager are better answers than a guess, and 0,0 is a legitimate screen corner.
        Assert.Null(viewModel.RestoreWindowSize);
        Assert.Null(viewModel.CompactWindowPosition);
    }

    [Fact]
    public void TheSizeToRestoreAndTheStripsPlace_SurviveARestart()
    {
        var viewModel = Build();

        viewModel.SaveRestoreWindowSize(1440, 900);
        viewModel.SaveCompactWindowPosition(120, 48);

        var reopened = Build();
        Assert.Equal((1440d, 900d), reopened.RestoreWindowSize);
        Assert.Equal((120, 48), reopened.CompactWindowPosition);
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(1280, 0)]
    [InlineData(double.NaN, 800)]
    [InlineData(1280, double.PositiveInfinity)]
    public void AnUnmeasuredWindowIsNotRecordedAsTheSizeToRestore(double width, double height)
    {
        var viewModel = Build();
        viewModel.SaveRestoreWindowSize(1440, 900);

        // Avalonia hands out NaN for a size the window has not been laid out to yet. Storing one
        // would leave the expand button restoring to nothing at all, so a bad measurement is
        // dropped rather than allowed to destroy the good value already on disk.
        viewModel.SaveRestoreWindowSize(width, height);

        Assert.Equal((1440d, 900d), Build().RestoreWindowSize);
    }

    [Fact]
    public void TheStripCanBeParkedAtTheTopLeftCorner()
    {
        var viewModel = Build();

        viewModel.SaveCompactWindowPosition(0, 0);

        // Guards the sentinel choice: if "unset" were 0 instead of int.MinValue, a strip dragged
        // into the corner would come back somewhere else entirely on the next launch.
        Assert.Equal((0, 0), Build().CompactWindowPosition);
    }
}
