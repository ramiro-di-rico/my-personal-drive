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
/// docs/PLAN-BROWSER-VIEWS.md M3/M4/M5 as the user meets them. The scan takes minutes, so the
/// behaviors worth pinning are about what happens around that: the command is unavailable without a
/// scanner, a completed scan is persisted and a cancelled one is not, and a result that arrives
/// after the user has navigated elsewhere does not overwrite the panel for the folder they are
/// actually looking at.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowDeepMetricsTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.DeepMetrics").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-deep-{Guid.NewGuid():N}.db");

    public MainWindowDeepMetricsTests()
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

    private static string FileJson(string uid, string name, long claimedSize)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z",
              "activeRevision": {
                "ok": true,
                "value": {
                  "claimedSize": {{claimedSize}},
                  "claimedModificationTime": "2026-01-01T00:00:00.000Z",
                  "claimedDigests": { "sha1": "hash-{{uid}}" }
                }
              }
            }
            """;

    private static string FolderJson(string uid, string name)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "folder", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z"
            }
            """;

    private (MainWindowViewModel ViewModel, FakeCliExecutor Executor, FolderMetricsStore Store) Build(
        bool withScanner = true, bool isAuthenticated = true)
    {
        // IsAuthenticated is private-set and read from settings.json at construction, so the only
        // way in is through the settings file the view model will load.
        new AppSettingsService().Save(new AppSettings
        {
            CliPath = "/usr/bin/proton-drive",
            IsAuthenticated = isAuthenticated,
        });

        var executor = new FakeCliExecutor();
        var service = new ProtonDriveService(executor);
        var syncStore = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(service, syncStore, new LocalScanner(), new RemoteScanner(service));
        var panel = new SyncPanelViewModel(syncStore, syncExecutor, new SyncCrashRecovery(syncStore));
        var metricsStore = new FolderMetricsStore(_dbPath);
        var viewModel = new MainWindowViewModel(
            service,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel,
            metricsStore: metricsStore,
            statsScanner: withScanner ? new FolderStatsScanner(service) : null);

        return (viewModel, executor, metricsStore);
    }

    [Fact]
    public void WithoutAScanner_TheDeepScanCommandIsUnavailable()
    {
        var (viewModel, _, _) = Build(withScanner: false);

        Assert.False(viewModel.ScanFolderDeeplyCommand.CanExecute(null));
    }

    [Fact]
    public void WhileNotAuthenticated_TheDeepScanCommandIsUnavailable()
    {
        var (viewModel, _, _) = Build(isAuthenticated: false);

        Assert.False(viewModel.ScanFolderDeeplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACompletedScan_ShowsRecursiveTotals_AndPersistsThem()
    {
        var (viewModel, executor, store) = Build();
        executor.RespondForPath("/my-files", $"[{FolderJson("u-sub", "Fotos")}, {FileJson("u-a", "a.pdf", 1000)}]");
        executor.RespondForPath("/my-files/Fotos", $"[{FileJson("u-b", "b.jpg", 2048)}]");

        await viewModel.ScanFolderDeeplyCommand.ExecuteAsync();

        Assert.True(viewModel.Metrics.IsDeep);
        Assert.False(viewModel.Metrics.IsPartial);
        Assert.Equal("3.0 KB", viewModel.Metrics.TotalSizeText);
        Assert.Contains("Recursivo", viewModel.Metrics.DepthNote);

        var stored = await store.GetAsync("/my-files");
        Assert.NotNull(stored);
        Assert.Equal(3048, stored!.TotalSize);
    }

    [Fact]
    public async Task ACancelledScan_IsShownAsPartial_AndNotPersisted()
    {
        var (viewModel, executor, store) = Build();
        executor.EnqueueOutput(_ =>
        {
            // Cancel from inside the first CLI call: deterministic, unlike cancelling from the
            // progress callback, which Progress<T> delivers on the thread pool.
            viewModel.CancelDeepScanCommand.ExecuteAsync().GetAwaiter().GetResult();
            return $"[{FolderJson("u-sub", "Fotos")}, {FileJson("u-a", "a.pdf", 1000)}]";
        });

        await viewModel.ScanFolderDeeplyCommand.ExecuteAsync();

        Assert.True(viewModel.Metrics.IsPartial);
        Assert.Contains("Parcial", viewModel.Metrics.DepthNote);
        Assert.Null(await store.GetAsync("/my-files"));
    }

    [Fact]
    public async Task WhileScanning_TheCommandCannotStartASecondScan()
    {
        var (viewModel, executor, _) = Build();
        var sawScanRunning = false;
        executor.EnqueueOutput(_ =>
        {
            // One scan at a time, app-wide: two would compete for the executor's concurrency
            // ceiling with each other, with the sync engine, and with the user's own browsing.
            sawScanRunning = viewModel.IsDeepScanRunning && !viewModel.ScanFolderDeeplyCommand.CanExecute(null);
            return "[]";
        });

        await viewModel.ScanFolderDeeplyCommand.ExecuteAsync();

        Assert.True(sawScanRunning);
        Assert.False(viewModel.IsDeepScanRunning);
        Assert.True(viewModel.ScanFolderDeeplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedScan_ReportsTheCliError_AndClearsTheScanningState()
    {
        var (viewModel, executor, _) = Build();
        executor.EnqueueFailure(new CliException(
            "filesystem list --json /my-files", exitCode: 1, stdout: string.Empty,
            stderr: "boom", message: "The CLI failed.", CliErrorKind.Unknown));

        await viewModel.ScanFolderDeeplyCommand.ExecuteAsync();

        Assert.True(viewModel.IsWarning);
        Assert.False(viewModel.IsDeepScanRunning);
        Assert.False(viewModel.Metrics.IsScanning);
    }

    [Fact]
    public async Task TrashingAFile_InvalidatesTheAncestorMetrics()
    {
        var (viewModel, executor, store) = Build();
        await store.SaveAsync(new FolderMetrics("/my-files", true, true, 1, 0, 5000, 0, [], [], null, null, 1,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
        executor.EnqueueOutput("{}");
        executor.RespondForPath("/my-files", "[]");

        await viewModel.TrashItemAsync(new DriveItem("/my-files/big.bin", "big.bin", false, 4000));

        // The stored 5 KB total counted a file that no longer exists; a number that is now wrong
        // has to go, not linger until someone re-scans.
        Assert.Null(await store.GetAsync("/my-files"));
    }
}
