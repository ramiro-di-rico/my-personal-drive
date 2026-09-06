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
/// docs/PLAN-UX-ROUND-3.md X7, the console's drag handle — round 1's Task 4 asked for it and only
/// the collapse toggle shipped, leaving the body at a hard-coded 140px ever since.
///
/// The drag itself is pointer plumbing in the view; what belongs here is everything the view model
/// owns: the direction, the limits, and the fact that the size survives a restart. The clamp is the
/// part worth pinning — a console dragged past the bottom of the window is unrecoverable without
/// editing settings.json by hand.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowConsoleHeightTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ConsoleHeight").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-console-{Guid.NewGuid():N}.db");

    public MainWindowConsoleHeightTests()
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
    public void DraggingTheHandleUp_MakesTheConsoleTaller()
    {
        var viewModel = Build();
        var before = viewModel.CommandConsoleHeight;

        // Screen coordinates grow downwards, so a negative delta is an upward drag.
        viewModel.ResizeCommandConsole(-40);

        Assert.Equal(before + 40, viewModel.CommandConsoleHeight);
    }

    [Fact]
    public void DraggingPastTheLimits_StopsAtThem()
    {
        var viewModel = Build();

        viewModel.ResizeCommandConsole(-10_000);
        Assert.Equal(AppSettings.MaxCommandConsoleHeight, viewModel.CommandConsoleHeight);

        viewModel.ResizeCommandConsole(10_000);
        Assert.Equal(AppSettings.MinCommandConsoleHeight, viewModel.CommandConsoleHeight);
    }

    [Fact]
    public void TheHeightSurvivesARestart()
    {
        var viewModel = Build();
        viewModel.ResizeCommandConsole(-30);
        var expected = viewModel.CommandConsoleHeight;

        Assert.Equal(expected, new AppSettingsService().Load().CommandConsoleHeightOrDefault());
        Assert.Equal(expected, Build().CommandConsoleHeight);
    }

    /// <summary>
    /// The value is read back from a file a user can edit, so the clamp lives on the read and not
    /// only on the drag. Asserted against the settings object directly: a NaN cannot even be
    /// written — System.Text.Json refuses it — so a round trip would test the serializer instead.
    /// </summary>
    [Fact]
    public void AnImpossibleHeightFallsBackToTheDefault()
    {
        Assert.Equal(
            AppSettings.DefaultCommandConsoleHeight,
            new AppSettings { CommandConsoleHeight = double.NaN }.CommandConsoleHeightOrDefault());
    }

    [Fact]
    public void AHeightEditedOutOfRangeIsBroughtBackIn()
    {
        var settings = new AppSettingsService();
        settings.Save(new AppSettings { CommandConsoleHeight = 5_000 });

        Assert.Equal(AppSettings.MaxCommandConsoleHeight, settings.Load().CommandConsoleHeightOrDefault());
        Assert.Equal(AppSettings.MaxCommandConsoleHeight, Build().CommandConsoleHeight);
    }

    /// <summary>
    /// The collapse animation runs on MaxHeight and the body on Height; if they drift, collapsing
    /// and reopening the console gives back a different size than the one that was dragged.
    /// </summary>
    [Fact]
    public async Task ReopeningTheConsole_RestoresTheDraggedHeight()
    {
        var viewModel = Build();
        viewModel.ResizeCommandConsole(-60);
        var dragged = viewModel.CommandConsoleHeight;

        await viewModel.ToggleCommandConsoleCommand.ExecuteAsync();
        await viewModel.ToggleCommandConsoleCommand.ExecuteAsync();

        Assert.Equal(dragged, viewModel.CommandConsoleHeight);
        Assert.True(viewModel.CommandConsoleMaxHeight >= dragged);
    }
}
