using Microsoft.Data.Sqlite;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>Task 4 (docs/INTERFACE_IMPROVEMENT_PLAN.md): the CLI activity panel's persisted collapse state, log filter, and search.</summary>
[Collection(AppDataCollection.Name)]
public class MainWindowActivityPanelTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ActivityPanel").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-activitypanel-{Guid.NewGuid():N}.db");

    public MainWindowActivityPanelTests()
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

    private MainWindowViewModel Build(AppSettingsService? settings = null)
    {
        settings ??= new AppSettingsService();
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            settings,
            panel);
    }

    [Fact]
    public async Task ConsoleVisibility_TogglesAndPersists_AndRestoresOnNextLaunch()
    {
        var sut = Build();
        Assert.True(sut.Console.IsCommandConsoleVisible);
        Assert.Equal(180, sut.Console.CommandConsoleMaxHeight);

        await sut.Console.ToggleCommandConsoleCommand.ExecuteAsync();

        Assert.False(sut.Console.IsCommandConsoleVisible);
        Assert.Equal(0, sut.Console.CommandConsoleMaxHeight);
        Assert.Equal("Show the CLI activity", sut.Console.CommandConsoleToggleLabel);
        Assert.False(new AppSettingsService().Load().ShowCommandConsole);

        // A fresh instance starts collapsed too — including the visual state that mirrors it,
        // not just the flag — since the persisted value is also next launch's default.
        var relaunched = Build();
        Assert.False(relaunched.Console.IsCommandConsoleVisible);
        Assert.Equal(0, relaunched.Console.CommandConsoleMaxHeight);
        Assert.Equal(0, relaunched.Console.CommandConsoleOpacity);
        Assert.False(relaunched.Console.CommandConsoleHitTestVisible);
        Assert.Equal("Show the CLI activity", relaunched.Console.CommandConsoleToggleLabel);
    }

    [Fact]
    public void LogFilter_ShowsOnlyWarningLinesAndErrorLines_WhenToggled()
    {
        var sut = Build();
        sut.Console.AppendCommandLogLinesForTests(
        [
            "[Proton Drive] > filesystem list --json /my-files",
            "[Proton Drive] [warn] falling back to text parser",
            "[Proton Drive] [err] connection refused",
            "[Proton Drive] [done] exit 0",
        ]);

        sut.Console.ShowOnlyWarningsAndErrors = true;

        Assert.Contains("[warn] falling back to text parser", sut.Console.CommandLogText);
        Assert.Contains("[err] connection refused", sut.Console.CommandLogText);
        Assert.DoesNotContain("filesystem list --json", sut.Console.CommandLogText);
        Assert.DoesNotContain("[done] exit 0", sut.Console.CommandLogText);

        sut.Console.ShowOnlyWarningsAndErrors = false;
        Assert.Contains("[done] exit 0", sut.Console.CommandLogText);
    }

    [Fact]
    public void LogSearch_FiltersLinesByCaseInsensitiveSubstring()
    {
        var sut = Build();
        sut.Console.AppendCommandLogLinesForTests(
        [
            "[Proton Drive] > filesystem list --json /my-files/Photos",
            "[OneDrive] > GET /root/delta",
        ]);

        sut.Console.LogSearchText = "photos";

        Assert.Contains("/my-files/Photos", sut.Console.CommandLogText);
        Assert.DoesNotContain("/root/delta", sut.Console.CommandLogText);
    }

    [Fact]
    public void SearchAndWarningsFilter_CombineAsAnAnd()
    {
        var sut = Build();
        sut.Console.AppendCommandLogLinesForTests(
        [
            "[Proton Drive] [warn] Photos: falling back to text parser",
            "[Proton Drive] [warn] Documents: falling back to text parser",
            "[Proton Drive] > filesystem list --json /my-files/Photos",
        ]);

        sut.Console.ShowOnlyWarningsAndErrors = true;
        sut.Console.LogSearchText = "photos";

        Assert.Contains("Photos: falling back", sut.Console.CommandLogText);
        Assert.DoesNotContain("Documents: falling back", sut.Console.CommandLogText);
        Assert.DoesNotContain("filesystem list --json", sut.Console.CommandLogText); // matches search but not the warning filter
    }

    [Fact]
    public async Task ClearActivity_ResetsLastLogLine()
    {
        var sut = Build();
        sut.Console.AppendCommandLogLinesForTests(["[Proton Drive] [done] exit 0"]);
        Assert.NotNull(sut.Console.LastLogLine);

        await sut.Console.ClearActivityCommand.ExecuteAsync();

        Assert.Null(sut.Console.LastLogLine);
        Assert.Equal("No CLI command is running.", sut.Console.CommandLogText);
    }
}
