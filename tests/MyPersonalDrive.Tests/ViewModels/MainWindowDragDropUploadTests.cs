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

/// <summary>Task 5 Phase 2 (docs/INTERFACE_IMPROVEMENT_PLAN.md): a local pane row dropped onto the cloud pane.</summary>
[Collection(AppDataCollection.Name)]
public class MainWindowDragDropUploadTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.DragDropUpload").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-dragdropupload-{Guid.NewGuid():N}.db");
    private readonly FakeCliExecutor _cli = new();

    public MainWindowDragDropUploadTests()
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
        var service = new ProtonDriveService(_cli);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel);
    }

    [Fact]
    public async Task DroppingOntoTheCurrentFolder_WithNoConflicts_EnqueuesAndDoesNotPromptTheDialog()
    {
        var sut = Build();
        var promptCalls = 0;
        sut.RequestConflictStrategyAsync = _ =>
        {
            promptCalls++;
            return Task.FromResult(UploadConflictStrategy.KeepBoth);
        };
        // Dropping onto CurrentPath also fires a background refresh (matching UploadAsync's own
        // "refresh in background" behavior) — a second queued response covers that extra call
        // regardless of whether it wins the race against this method's own assertions.
        _cli.EnqueueOutput(string.Empty);
        _cli.EnqueueOutput("[]");

        await sut.HandleLocalFilesDroppedAsync(["/home/user/report.pdf"], sut.CurrentPath);

        Assert.Equal(0, promptCalls);
        var upload = Assert.Single(sut.TransferQueue.Items);
        Assert.Equal(TransferDirection.Upload, upload.Direction);
        Assert.Equal(TransferStatus.Done, upload.Status);
        Assert.Equal(sut.CurrentPath, upload.TargetLabel);
        Assert.Contains(_cli.Calls, call => call.Arguments.Contains("upload"));
    }

    [Fact]
    public async Task DroppingAFileThatAlreadyExistsInTheCurrentFolder_PromptsOnceForTheWholeBatch()
    {
        var sut = Build();
        sut.DisplayItems([new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false)]);

        var promptedNames = new List<string>();
        sut.RequestConflictStrategyAsync = conflicts =>
        {
            promptedNames.AddRange(conflicts);
            return Task.FromResult(UploadConflictStrategy.Replace);
        };
        _cli.EnqueueOutput(string.Empty); // the upload
        _cli.EnqueueOutput("[]"); // the background refresh dropping onto CurrentPath triggers

        await sut.HandleLocalFilesDroppedAsync(["/home/user/report.pdf", "/home/user/new.txt"], sut.CurrentPath);

        Assert.Equal(["report.pdf"], promptedNames);
        Assert.Equal(TransferStatus.Done, Assert.Single(sut.TransferQueue.Items).Status);
    }

    [Fact]
    public async Task CancellingTheConflictDialog_EnqueuesNothing()
    {
        var sut = Build();
        sut.DisplayItems([new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false)]);
        sut.RequestConflictStrategyAsync = _ => Task.FromResult(UploadConflictStrategy.None);

        await sut.HandleLocalFilesDroppedAsync(["/home/user/report.pdf"], sut.CurrentPath);

        Assert.Empty(sut.TransferQueue.Items);
        Assert.Equal("Subida cancelada.", sut.StatusMessage);
    }

    [Fact]
    public async Task DroppingOntoADifferentFolder_SkipsTheConflictCheck_AndTargetsThatFolder()
    {
        var sut = Build();
        // A conflicting name in the *currently loaded* folder must not block a drop aimed at a
        // different folder row — the pre-check only covers what's already in memory (CurrentPath).
        sut.DisplayItems([new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false)]);
        var promptCalls = 0;
        sut.RequestConflictStrategyAsync = _ => { promptCalls++; return Task.FromResult(UploadConflictStrategy.None); };
        _cli.EnqueueOutput(string.Empty);

        await sut.HandleLocalFilesDroppedAsync(["/home/user/report.pdf"], "/my-files/Documents");

        Assert.Equal(0, promptCalls);
        var upload = Assert.Single(sut.TransferQueue.Items);
        Assert.Equal("/my-files/Documents", upload.TargetLabel);
        Assert.Equal(TransferStatus.Done, upload.Status);
    }
}
