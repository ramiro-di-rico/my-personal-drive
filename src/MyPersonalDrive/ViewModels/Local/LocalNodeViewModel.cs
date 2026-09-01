using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.ViewModels.Local;

/// <summary>
/// One row in the local pane. Navigation-only until docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6 gave
/// it a context menu (<see cref="LocalNodeSyncActions"/>) — download/upload still don't belong
/// here (drag-and-drop already covers file transfer for this pane), but copy-path, rename, delete,
/// and sync-pair management do.
/// </summary>
public sealed class LocalNodeViewModel : ObservableObject
{
    private readonly LocalNodeSyncActions? _syncActions;

    public LocalNodeViewModel(DriveItem item, Func<DriveItem, Task> navigateAsync, Action<Exception>? onError = null, LocalNodeSyncActions? syncActions = null)
    {
        Item = item;
        FileKind = FileKindClassifier.Classify(item.Name, item.IsFolder);
        _syncActions = syncActions;
        SyncPair = syncActions?.FindSyncPair?.Invoke(item);
        RowCommand = new AsyncCommand(() => IsFolder ? navigateAsync(Item) : Task.CompletedTask, onError: onError);
        CopyPathCommand = new AsyncCommand(CopyPathAsync, () => _syncActions?.CopyPathAsync is not null, onError);
        RenameCommand = new AsyncCommand(RenameAsync, () => _syncActions?.RenameAsync is not null, onError);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => _syncActions?.DeleteAsync is not null, onError);
        SyncSelectedPathCommand = new AsyncCommand(SyncSelectedPathAsync, () => CanCreateSyncPair && _syncActions?.SyncSelectedPathAsync is not null, onError);
        TogglePauseSyncCommand = new AsyncCommand(TogglePauseSyncAsync, () => SyncPair is not null, onError);
        PropertiesCommand = new AsyncCommand(ShowPropertiesAsync, () => _syncActions?.ShowPropertiesAsync is not null, onError);
    }

    public DriveItem Item { get; }

    public bool IsFolder => Item.IsFolder;

    public string DisplayName => string.IsNullOrWhiteSpace(Item.Name) ? Item.Path : Item.Name;

    public FileKind FileKind { get; }

    public string? SizeText => Item.Size is null ? null : ByteSize.Format(Item.Size.Value);

    public string? ModifiedText => Item.ModifiedAt?.ToLocalTime().ToString("g");

    /// <summary>The configured sync pair whose local side is this row, or null if none exists.</summary>
    public SyncPairViewModel? SyncPair { get; }

    public bool HasSyncPair => SyncPair is not null;

    public bool IsSyncPairPaused => SyncPair?.IsPaused ?? false;

    public bool IsSyncPairActive => HasSyncPair && !IsSyncPairPaused;

    public bool CanCreateSyncPair => IsFolder && !HasSyncPair;

    public AsyncCommand RowCommand { get; }

    public AsyncCommand CopyPathCommand { get; }

    public AsyncCommand RenameCommand { get; }

    public AsyncCommand DeleteCommand { get; }

    public AsyncCommand SyncSelectedPathCommand { get; }

    public AsyncCommand TogglePauseSyncCommand { get; }

    public AsyncCommand PropertiesCommand { get; }

    private async Task CopyPathAsync()
    {
        if (_syncActions?.CopyPathAsync is { } copyPathAsync)
        {
            await copyPathAsync(Item);
        }
    }

    private async Task RenameAsync()
    {
        if (_syncActions?.RenameAsync is { } renameAsync)
        {
            await renameAsync(Item);
        }
    }

    private async Task DeleteAsync()
    {
        if (_syncActions?.DeleteAsync is { } deleteAsync)
        {
            await deleteAsync(Item);
        }
    }

    private async Task SyncSelectedPathAsync()
    {
        if (_syncActions?.SyncSelectedPathAsync is { } syncSelectedPathAsync)
        {
            await syncSelectedPathAsync(Item);
        }
    }

    private async Task TogglePauseSyncAsync()
    {
        if (SyncPair is null)
        {
            return;
        }

        await SyncPair.TogglePauseCommand.ExecuteAsync();

        if (_syncActions?.RefreshPaneAsync is { } refreshPaneAsync)
        {
            await refreshPaneAsync();
        }
    }

    private async Task ShowPropertiesAsync()
    {
        if (_syncActions?.ShowPropertiesAsync is { } showPropertiesAsync)
        {
            await showPropertiesAsync(Item);
        }
    }
}
