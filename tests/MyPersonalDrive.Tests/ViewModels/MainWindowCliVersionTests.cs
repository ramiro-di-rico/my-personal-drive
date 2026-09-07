using Microsoft.Data.Sqlite;
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

/// <summary>
/// The CLI version readout in the settings view. The point of these tests is that a failing
/// `--version` is reported on screen rather than thrown: the flag has never been verified against
/// a real `proton-drive` build, so "the CLI rejected it" is an outcome the UI has to survive.
///
/// XDG_CONFIG_HOME is redirected for the same reason as in <see cref="Services.AppSettingsServiceTests"/> —
/// <see cref="MainWindowViewModel"/> persists on every CliPath change, and that must not land in
/// the developer's real settings.json.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowCliVersionTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.CliVersion").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-cli-version-{Guid.NewGuid():N}.db");

    public MainWindowCliVersionTests()
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

    private (MainWindowViewModel ViewModel, FakeCliExecutor Executor) Build(
        ICliReleaseFeed? releaseFeed = null,
        CliUpdateInstaller? installer = null,
        string cliPath = "/usr/bin/proton-drive")
    {
        var executor = new FakeCliExecutor();
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var viewModel = new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel,
            releaseFeed: releaseFeed,
            updateInstaller: installer)
        {
            CliPath = cliPath
        };

        return (viewModel, executor);
    }

    [Fact]
    public async Task CheckCliVersion_ShowsWhatTheCliReported()
    {
        var (viewModel, executor) = Build();
        executor.EnqueueOutput("proton-drive 1.2.3\n");

        await viewModel.CliUpdate.CheckCliVersionCommand.ExecuteAsync();

        Assert.Equal("proton-drive 1.2.3", viewModel.CliUpdate.CliVersion);
        Assert.Equal(["--version"], Assert.Single(executor.Calls).Arguments);
        Assert.False(viewModel.CliUpdate.IsCheckingCliVersion);
    }

    [Fact]
    public async Task CheckCliVersion_WhenTheCliRejectsTheFlag_SurfacesTheErrorInsteadOfThrowing()
    {
        var (viewModel, executor) = Build();
        executor.EnqueueFailure(new DriveException(
            "--version",
            exitCode: 1,
            stdout: string.Empty,
            stderr: "unknown flag: --version",
            message: "unknown flag: --version",
            kind: DriveErrorKind.InvalidArgument));

        await viewModel.CliUpdate.CheckCliVersionCommand.ExecuteAsync();

        Assert.Contains("unknown flag: --version", viewModel.CliUpdate.CliVersion);
        Assert.False(viewModel.CliUpdate.IsCheckingCliVersion);
    }

    [Fact]
    public async Task ChangingTheCliPath_DiscardsThePreviouslyReadVersion()
    {
        var (viewModel, executor) = Build();
        executor.EnqueueOutput("proton-drive 1.2.3");
        await viewModel.CliUpdate.CheckCliVersionCommand.ExecuteAsync();

        viewModel.CliPath = "/opt/other/proton-drive";

        Assert.Equal("Unknown", viewModel.CliUpdate.CliVersion);
    }

    [Fact]
    public async Task CheckCliVersion_WithNoCliPath_DoesNotLaunchTheCli()
    {
        var (viewModel, executor) = Build();
        viewModel.CliPath = string.Empty;

        await viewModel.CliUpdate.CheckCliVersionCommand.ExecuteAsync();

        Assert.Empty(executor.Calls);
        Assert.Equal("Unknown", viewModel.CliUpdate.CliVersion);
    }
}
