using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.ViewModels;

public sealed class DriveNodeViewModel : ObservableObject
{
    private readonly Func<DriveItem, Task> _handleRowClickAsync;
    private readonly Func<DriveItem, Task> _downloadItemAsync;
    private readonly Func<DriveItem, Task> _trashItemAsync;
    private readonly Func<DriveItem, Task> _renameItemAsync;
    private readonly Func<DriveItem, Task> _copyItemAsync;
    private readonly Func<DriveItem, Task>? _previewItemAsync;
    private readonly DriveNodeSyncActions? _syncActions;
    private bool _isSelected;
    private string? _deepSizeText;

    public DriveNodeViewModel(DriveItem item, Func<DriveItem, Task> handleRowClickAsync, Func<DriveItem, Task> downloadItemAsync, Func<DriveItem, Task> trashItemAsync, Func<DriveItem, Task> renameItemAsync, Func<DriveItem, Task> copyItemAsync, Func<DriveItem, Task>? previewItemAsync = null, Action<Exception>? onError = null, DriveNodeSyncActions? syncActions = null)
    {
        Item = item;
        FileKind = FileKindClassifier.Classify(item.Name, item.IsFolder);
        _handleRowClickAsync = handleRowClickAsync;
        _downloadItemAsync = downloadItemAsync;
        _trashItemAsync = trashItemAsync;
        _renameItemAsync = renameItemAsync;
        _copyItemAsync = copyItemAsync;
        _previewItemAsync = previewItemAsync;
        _syncActions = syncActions;
        SyncPair = syncActions?.FindSyncPair?.Invoke(item);
        CanPreview = TextPreviewPolicy.CanPreview(item) || ImagePreviewPolicy.CanPreview(item) || PdfPreviewPolicy.CanPreview(item);
        RowCommand = new AsyncCommand(HandleRowClickAsync, onError: onError);
        DownloadCommand = new AsyncCommand(DownloadAsync, () => !Item.IsFolder, onError);
        TrashCommand = new AsyncCommand(TrashAsync, onError: onError);
        RenameCommand = new AsyncCommand(RenameAsync, onError: onError);
        CopyCommand = new AsyncCommand(CopyAsync, onError: onError);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => CanPreview && _previewItemAsync is not null, onError);
        CopyPathCommand = new AsyncCommand(CopyPathAsync, () => _syncActions?.CopyPathAsync is not null, onError);
        UploadToFolderCommand = new AsyncCommand(UploadToFolderAsync, () => IsFolder && _syncActions?.UploadToFolderAsync is not null, onError);
        DownloadHereCommand = new AsyncCommand(DownloadHereAsync, () => _syncActions?.DownloadHereAsync is not null, onError);
        SyncSelectedPathCommand = new AsyncCommand(SyncSelectedPathAsync, () => CanCreateSyncPair && _syncActions?.SyncSelectedPathAsync is not null, onError);
        TogglePauseSyncCommand = new AsyncCommand(TogglePauseSyncAsync, () => SyncPair is not null, onError);
        PropertiesCommand = new AsyncCommand(ShowPropertiesAsync, () => _syncActions?.ShowPropertiesAsync is not null, onError);
    }

    public DriveItem Item { get; }

    public bool IsFolder => Item.IsFolder;

    public bool IsFile => !Item.IsFolder;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Item.Name) ? Item.Path : Item.Name;

    public string Path => Item.Path;

    /// <summary>
    /// Whether the in-app viewer offers itself for this row — text via <see cref="TextPreviewPolicy"/>
    /// or an image via <see cref="ImagePreviewPolicy"/>. Which one it is gets decided again, from
    /// the same policies, where the download actually happens
    /// (<c>MainWindowViewModel.PreviewItemAsync</c>) — this flag only drives whether the button
    /// shows up. Computed once per row like <see cref="FileKind"/>: both policies are pure, and
    /// every refresh rebuilds the rows anyway.
    /// </summary>
    public bool CanPreview { get; }

    public string Kind => Item.IsFolder ? "Folder" : "File";

    /// <summary>
    /// What the row is, for the icon the item templates draw (via
    /// <c>Views.Converters.FileKindIconConverter</c>) and for the metrics histogram. Computed once
    /// per row: the classifier is pure, but the listing rebuilds every row on each refresh.
    /// </summary>
    public FileKind FileKind { get; }

    public string? SizeText => Item.Size is null ? null : $"{Item.Size:n0} bytes";

    /// <summary>
    /// A folder's recursive size, once someone has paid for the scan that produced it
    /// (docs/PLAN-BROWSER-VIEWS.md M4/M5). Null for files, and for folders nobody has analyzed —
    /// the browser gradually learns these rather than computing them on sight, because each one
    /// costs ~3.5 s per subfolder.
    /// </summary>
    public string? DeepSizeText
    {
        get => _deepSizeText;
        set
        {
            if (SetProperty(ref _deepSizeText, value))
            {
                OnPropertyChanged(nameof(HasDeepSize));
            }
        }
    }

    public bool HasDeepSize => !string.IsNullOrEmpty(DeepSizeText);

    public string? ModifiedText => Item.ModifiedAt?.ToLocalTime().ToString("g");

    public string? OwnerText => Item.Owner;

    public bool SharedText => Item.IsShared;

    /// <summary>The configured sync pair whose remote side is this row, or null if none exists (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6).</summary>
    public SyncPairViewModel? SyncPair { get; }

    public bool HasSyncPair => SyncPair is not null;

    public bool IsSyncPairPaused => SyncPair?.IsPaused ?? false;

    public bool IsSyncPairActive => HasSyncPair && !IsSyncPairPaused;

    /// <summary>Whether "Sync Selected Path..." makes sense here — a folder with no pair on it yet.</summary>
    public bool CanCreateSyncPair => IsFolder && !HasSyncPair;

    public AsyncCommand RowCommand { get; }

    public AsyncCommand DownloadCommand { get; }

    public AsyncCommand TrashCommand { get; }

    public AsyncCommand RenameCommand { get; }

    public AsyncCommand CopyCommand { get; }

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand CopyPathCommand { get; }

    public AsyncCommand UploadToFolderCommand { get; }

    public AsyncCommand DownloadHereCommand { get; }

    public AsyncCommand SyncSelectedPathCommand { get; }

    public AsyncCommand TogglePauseSyncCommand { get; }

    public AsyncCommand PropertiesCommand { get; }

    private async Task HandleRowClickAsync()
    {
        await _handleRowClickAsync(Item);
    }

    private async Task DownloadAsync()
    {
        if (Item.IsFolder)
        {
            return;
        }

        await _downloadItemAsync(Item);
    }

    private async Task TrashAsync()
    {
        await _trashItemAsync(Item);
    }

    private async Task RenameAsync()
    {
        await _renameItemAsync(Item);
    }

    private async Task CopyAsync()
    {
        await _copyItemAsync(Item);
    }

    private async Task PreviewAsync()
    {
        if (!CanPreview || _previewItemAsync is null)
        {
            return;
        }

        await _previewItemAsync(Item);
    }

    private async Task CopyPathAsync()
    {
        if (_syncActions?.CopyPathAsync is { } copyPathAsync)
        {
            await copyPathAsync(Item);
        }
    }

    private async Task UploadToFolderAsync()
    {
        if (_syncActions?.UploadToFolderAsync is { } uploadToFolderAsync)
        {
            await uploadToFolderAsync(Item);
        }
    }

    private async Task DownloadHereAsync()
    {
        if (_syncActions?.DownloadHereAsync is { } downloadHereAsync)
        {
            await downloadHereAsync(Item);
        }
    }

    private async Task SyncSelectedPathAsync()
    {
        if (_syncActions?.SyncSelectedPathAsync is { } syncSelectedPathAsync)
        {
            await syncSelectedPathAsync(Item);
        }
    }

    /// <summary>
    /// Wraps <see cref="SyncPairViewModel.TogglePauseCommand"/> (bound to directly for
    /// <c>SyncNowCommand</c>, which doesn't need this) with a pane refresh: this row was built with
    /// a snapshot of <see cref="SyncPair"/>'s paused state at load time, so nothing else would
    /// notice the row-level <see cref="IsSyncPairPaused"/>/<see cref="IsSyncPairActive"/> badges
    /// changed.
    /// </summary>
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
