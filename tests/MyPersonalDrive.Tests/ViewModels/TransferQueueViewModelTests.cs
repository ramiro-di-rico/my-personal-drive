using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.ViewModels;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>Task 5 (docs/INTERFACE_IMPROVEMENT_PLAN.md): the drag-and-drop transfer queue.</summary>
public class TransferQueueViewModelTests
{
    [Fact]
    public async Task EnqueueUpload_BatchesEveryPathIntoOneCall()
    {
        var ops = new FakeDriveOperations();
        var sut = new TransferQueueViewModel();

        await sut.EnqueueUpload(ops, ["/local/a.txt", "/local/b.txt"], "/my-files/Docs", UploadConflictStrategy.None);

        var upload = Assert.Single(ops.Uploads);
        Assert.Equal(["/local/a.txt", "/local/b.txt"], upload.Paths);
        Assert.Equal("/my-files/Docs", upload.Target);
        Assert.Single(sut.Items);
        Assert.Equal(TransferStatus.Done, sut.Items[0].Status);
        Assert.Equal("2 elementos", sut.Items[0].SourceLabel);
    }

    [Fact]
    public async Task EnqueueDownload_OneItemPerDraggedFile()
    {
        var ops = new FakeDriveOperations();
        var sut = new TransferQueueViewModel();

        await sut.EnqueueDownload(ops, new DriveItem("/my-files/a.txt", "a.txt", IsFolder: false), "/home/user/Downloads");
        await sut.EnqueueDownload(ops, new DriveItem("/my-files/b.txt", "b.txt", IsFolder: false), "/home/user/Downloads");

        Assert.Equal(2, ops.Downloads.Count);
        Assert.Equal(2, sut.Items.Count);
        Assert.All(sut.Items, i => Assert.Equal(TransferStatus.Done, i.Status));
    }

    [Fact]
    public async Task FailedTransfer_SurfacesTheErrorMessage_WithoutStoppingTheQueue()
    {
        var ops = new FakeDriveOperations();
        var failure = new DriveException("filesystem download", 1, string.Empty, "not found", "not found", DriveErrorKind.NotFound);
        ops.NextCallThrows = failure;
        var sut = new TransferQueueViewModel();

        await sut.EnqueueDownload(ops, new DriveItem("/my-files/missing.txt", "missing.txt", IsFolder: false), "/home/user/Downloads");
        await sut.EnqueueDownload(ops, new DriveItem("/my-files/ok.txt", "ok.txt", IsFolder: false), "/home/user/Downloads");

        Assert.Equal(TransferStatus.Failed, sut.Items[0].Status);
        Assert.Equal("not found", sut.Items[0].ErrorMessage);
        Assert.Equal(TransferStatus.Done, sut.Items[1].Status); // the queue kept going after the failure
    }

    [Fact]
    public async Task CancellingAQueuedItem_SkipsItWithoutCallingTheOperation()
    {
        var ops = new FakeDriveOperations();
        // Block the first item's call so the second one is still Queued when we cancel it.
        var gate = new TaskCompletionSource();
        ops.NextCallBlocksOn = gate.Task;

        var sut = new TransferQueueViewModel();
        var first = sut.EnqueueDownload(ops, new DriveItem("/my-files/a.txt", "a.txt", IsFolder: false), "/home/user/Downloads");
        var second = sut.EnqueueDownload(ops, new DriveItem("/my-files/b.txt", "b.txt", IsFolder: false), "/home/user/Downloads");

        Assert.Equal(TransferStatus.Queued, sut.Items[1].Status);
        await sut.Items[1].CancelCommand.ExecuteAsync();
        Assert.Equal(TransferStatus.Cancelled, sut.Items[1].Status);

        gate.SetResult();
        await first;
        await second;

        Assert.Single(ops.Downloads); // only item[0]'s download actually ran
    }

    [Fact]
    public async Task CancellingAnInFlightItem_StopsIt()
    {
        var ops = new FakeDriveOperations();
        ops.NextCallHonorsCancellation = true;

        var sut = new TransferQueueViewModel();
        var enqueue = sut.EnqueueDownload(ops, new DriveItem("/my-files/a.txt", "a.txt", IsFolder: false), "/home/user/Downloads");

        // Give ProcessQueueAsync a chance to reach Transferring before cancelling.
        while (sut.Items[0].Status != TransferStatus.Transferring)
        {
            await Task.Yield();
        }

        await sut.Items[0].CancelCommand.ExecuteAsync();
        await enqueue;

        Assert.Equal(TransferStatus.Cancelled, sut.Items[0].Status);
    }

    [Fact]
    public async Task Summary_ReflectsQueuedAndTransferringCounts()
    {
        var ops = new FakeDriveOperations();
        var gate = new TaskCompletionSource();
        ops.NextCallBlocksOn = gate.Task;

        var sut = new TransferQueueViewModel();
        Assert.Equal("Sin transferencias activas", sut.Summary);

        var first = sut.EnqueueDownload(ops, new DriveItem("/my-files/a.txt", "a.txt", IsFolder: false), "/dl");
        _ = sut.EnqueueDownload(ops, new DriveItem("/my-files/b.txt", "b.txt", IsFolder: false), "/dl");

        while (sut.Items[0].Status != TransferStatus.Transferring)
        {
            await Task.Yield();
        }

        Assert.Equal("1 transfiriendo · 1 en cola", sut.Summary);

        gate.SetResult();
        await first;
    }

    /// <summary>A minimal <see cref="IDriveOperations"/> the queue can be driven against directly, without a real CLI in the loop.</summary>
    private sealed class FakeDriveOperations : IDriveOperations
    {
        public List<(IReadOnlyList<string> Paths, string Target, UploadConflictStrategy Strategy)> Uploads { get; } = [];

        public List<(string Path, string Folder)> Downloads { get; } = [];

        public Exception? NextCallThrows { get; set; }

        public Task? NextCallBlocksOn { get; set; }

        public bool NextCallHonorsCancellation { get; set; }

        public Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
        {
            Downloads.Add((path, localFolder));
            await RunNextCallBehaviorAsync(cancellationToken);
        }

        public async Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default)
        {
            Uploads.Add((localPaths, parentPath, strategy));
            await RunNextCallBehaviorAsync(cancellationToken);
        }

        public Task TrashItemAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private async Task RunNextCallBehaviorAsync(CancellationToken cancellationToken)
        {
            if (NextCallThrows is { } ex)
            {
                NextCallThrows = null;
                throw ex;
            }

            if (NextCallHonorsCancellation)
            {
                NextCallHonorsCancellation = false;
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return;
            }

            if (NextCallBlocksOn is { } gate)
            {
                NextCallBlocksOn = null;
                await gate;
            }
        }
    }
}
