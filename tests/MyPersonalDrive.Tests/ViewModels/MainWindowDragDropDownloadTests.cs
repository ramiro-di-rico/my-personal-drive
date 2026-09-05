using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>Task 5 Phase 3 (docs/INTERFACE_IMPROVEMENT_PLAN.md): cloud pane rows dropped onto the local pane.</summary>
[Collection(AppDataCollection.Name)]
public class MainWindowDragDropDownloadTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.DragDropDownload").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-dragdropdownload-{Guid.NewGuid():N}.db");
    private readonly FakeCliExecutor _cli = new();

    public MainWindowDragDropDownloadTests()
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

    private MainWindowViewModel Build(FakeExistsLocalFileSystemService? localFileSystem = null)
    {
        var service = new ProtonDriveService(_cli);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel,
            localFileSystem: localFileSystem ?? new FakeExistsLocalFileSystemService(existingPaths: []));
    }

    [Fact]
    public async Task DroppingAFile_WithNoLocalConflict_DownloadsWithoutPrompting()
    {
        var sut = Build();
        var promptCalls = 0;
        sut.RequestConflictStrategyAsync = _ => { promptCalls++; return Task.FromResult(UploadConflictStrategy.KeepBoth); };
        _cli.EnqueueOutput(string.Empty);

        await sut.HandleCloudItemsDroppedAsync([new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false)], "/home/user/Downloads");

        Assert.Equal(0, promptCalls);
        var download = Assert.Single(sut.TransferQueue.Items);
        Assert.Equal(TransferDirection.Download, download.Direction);
        Assert.Equal(TransferStatus.Done, download.Status);
        Assert.Equal("/home/user/Downloads", download.TargetLabel);
        Assert.Contains(_cli.Calls, call => call.Arguments.Contains("download"));
    }

    [Fact]
    public async Task DroppingAFileThatAlreadyExistsLocally_PromptsOnceForTheWholeBatch()
    {
        var localFs = new FakeExistsLocalFileSystemService(existingPaths: ["/home/user/Downloads/report.pdf"]);
        var sut = Build(localFs);
        var promptedNames = new List<string>();
        sut.RequestConflictStrategyAsync = conflicts => { promptedNames.AddRange(conflicts); return Task.FromResult(UploadConflictStrategy.Replace); };
        _cli.EnqueueOutput(string.Empty);
        _cli.EnqueueOutput(string.Empty);

        await sut.HandleCloudItemsDroppedAsync(
        [
            new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false),
            new DriveItem("/my-files/new.txt", "new.txt", IsFolder: false),
        ], "/home/user/Downloads");

        Assert.Equal(["report.pdf"], promptedNames);
        Assert.Equal(2, sut.TransferQueue.Items.Count); // Replace still downloads both
        Assert.All(sut.TransferQueue.Items, i => Assert.Equal(TransferStatus.Done, i.Status));
    }

    [Fact]
    public async Task SkippingAConflict_DropsOnlyThatItemFromTheBatch()
    {
        var localFs = new FakeExistsLocalFileSystemService(existingPaths: ["/home/user/Downloads/report.pdf"]);
        var sut = Build(localFs);
        sut.RequestConflictStrategyAsync = _ => Task.FromResult(UploadConflictStrategy.Skip);
        _cli.EnqueueOutput(string.Empty); // only new.txt should actually download

        await sut.HandleCloudItemsDroppedAsync(
        [
            new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false),
            new DriveItem("/my-files/new.txt", "new.txt", IsFolder: false),
        ], "/home/user/Downloads");

        var item = Assert.Single(sut.TransferQueue.Items);
        Assert.Equal("new.txt", item.SourceLabel);
        Assert.Equal(TransferStatus.Done, item.Status);
    }

    [Fact]
    public async Task CancellingTheConflictDialog_EnqueuesNothing()
    {
        var localFs = new FakeExistsLocalFileSystemService(existingPaths: ["/home/user/Downloads/report.pdf"]);
        var sut = Build(localFs);
        sut.RequestConflictStrategyAsync = _ => Task.FromResult(UploadConflictStrategy.None);

        await sut.HandleCloudItemsDroppedAsync([new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false)], "/home/user/Downloads");

        Assert.Empty(sut.TransferQueue.Items);
        Assert.Equal("Download cancelled.", sut.StatusMessage);
    }

    [Fact]
    public async Task EachDraggedItem_IsItsOwnQueueEntry()
    {
        var sut = Build();
        _cli.EnqueueOutput(string.Empty);
        _cli.EnqueueOutput(string.Empty);

        await sut.HandleCloudItemsDroppedAsync(
        [
            new DriveItem("/my-files/a.txt", "a.txt", IsFolder: false),
            new DriveItem("/my-files/b.txt", "b.txt", IsFolder: false),
        ], "/home/user/Downloads");

        Assert.Equal(2, sut.TransferQueue.Items.Count);
        Assert.Equal(2, _cli.Calls.Count(c => c.Arguments.Contains("download")));
    }

    private sealed class FakeExistsLocalFileSystemService(HashSet<string> existingPaths) : LocalFileSystemService
    {
        public override bool Exists(string path) => existingPaths.Contains(path);
    }
}
