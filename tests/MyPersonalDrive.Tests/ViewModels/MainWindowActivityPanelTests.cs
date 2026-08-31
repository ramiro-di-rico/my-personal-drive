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
        Assert.True(sut.IsCommandConsoleVisible);
        Assert.Equal(180, sut.CommandConsoleMaxHeight);

        await sut.ToggleCommandConsoleCommand.ExecuteAsync();

        Assert.False(sut.IsCommandConsoleVisible);
        Assert.Equal(0, sut.CommandConsoleMaxHeight);
        Assert.Equal("Show CLI activity", sut.CommandConsoleToggleLabel);
        Assert.False(new AppSettingsService().Load().ShowCommandConsole);

        // A fresh instance starts collapsed too — including the visual state that mirrors it,
        // not just the flag — since the persisted value is also next launch's default.
        var relaunched = Build();
        Assert.False(relaunched.IsCommandConsoleVisible);
        Assert.Equal(0, relaunched.CommandConsoleMaxHeight);
        Assert.Equal(0, relaunched.CommandConsoleOpacity);
        Assert.False(relaunched.CommandConsoleHitTestVisible);
        Assert.Equal("Show CLI activity", relaunched.CommandConsoleToggleLabel);
    }

    [Fact]
    public void LogFilter_ShowsOnlyWarningLinesAndErrorLines_WhenToggled()
    {
        var sut = Build();
        sut.AppendCommandLogLinesForTests(
        [
            "[Proton Drive] > filesystem list --json /my-files",
            "[Proton Drive] [warn] falling back to text parser",
            "[Proton Drive] [err] connection refused",
            "[Proton Drive] [done] exit 0",
        ]);

        sut.ShowOnlyWarningsAndErrors = true;

        Assert.Contains("[warn] falling back to text parser", sut.CommandLogText);
        Assert.Contains("[err] connection refused", sut.CommandLogText);
        Assert.DoesNotContain("filesystem list --json", sut.CommandLogText);
        Assert.DoesNotContain("[done] exit 0", sut.CommandLogText);

        sut.ShowOnlyWarningsAndErrors = false;
        Assert.Contains("[done] exit 0", sut.CommandLogText);
    }

    [Fact]
    public void LogSearch_FiltersLinesByCaseInsensitiveSubstring()
    {
        var sut = Build();
        sut.AppendCommandLogLinesForTests(
        [
            "[Proton Drive] > filesystem list --json /my-files/Photos",
            "[OneDrive] > GET /root/delta",
        ]);

        sut.LogSearchText = "photos";

        Assert.Contains("/my-files/Photos", sut.CommandLogText);
        Assert.DoesNotContain("/root/delta", sut.CommandLogText);
    }

    [Fact]
    public void SearchAndWarningsFilter_CombineAsAnAnd()
    {
        var sut = Build();
        sut.AppendCommandLogLinesForTests(
        [
            "[Proton Drive] [warn] Photos: falling back to text parser",
            "[Proton Drive] [warn] Documents: falling back to text parser",
            "[Proton Drive] > filesystem list --json /my-files/Photos",
        ]);

        sut.ShowOnlyWarningsAndErrors = true;
        sut.LogSearchText = "photos";

        Assert.Contains("Photos: falling back", sut.CommandLogText);
        Assert.DoesNotContain("Documents: falling back", sut.CommandLogText);
        Assert.DoesNotContain("filesystem list --json", sut.CommandLogText); // matches search but not the warning filter
    }

    [Fact]
    public async Task ClearActivity_ResetsLastLogLine()
    {
        var sut = Build();
        sut.AppendCommandLogLinesForTests(["[Proton Drive] [done] exit 0"]);
        Assert.NotNull(sut.LastLogLine);

        await sut.ClearActivityCommand.ExecuteAsync();

        Assert.Null(sut.LastLogLine);
        Assert.Equal("No CLI command running.", sut.CommandLogText);
    }
}
