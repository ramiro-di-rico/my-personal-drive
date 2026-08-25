using MyPersonalDrive.Models;
using MyPersonalDrive.Services;

namespace MyPersonalDrive.ViewModels;

public sealed class DriveNodeViewModel : ObservableObject
{
    private readonly Func<DriveItem, Task> _handleRowClickAsync;
    private readonly Func<DriveItem, Task> _downloadItemAsync;
    private readonly Func<DriveItem, Task> _trashItemAsync;
    private readonly Func<DriveItem, Task> _renameItemAsync;
    private readonly Func<DriveItem, Task> _copyItemAsync;
    private bool _isSelected;
    private string? _deepSizeText;

    public DriveNodeViewModel(DriveItem item, Func<DriveItem, Task> handleRowClickAsync, Func<DriveItem, Task> downloadItemAsync, Func<DriveItem, Task> trashItemAsync, Func<DriveItem, Task> renameItemAsync, Func<DriveItem, Task> copyItemAsync, Action<Exception>? onError = null)
    {
        Item = item;
        FileKind = FileKindClassifier.Classify(item.Name, item.IsFolder);
        _handleRowClickAsync = handleRowClickAsync;
        _downloadItemAsync = downloadItemAsync;
        _trashItemAsync = trashItemAsync;
        _renameItemAsync = renameItemAsync;
        _copyItemAsync = copyItemAsync;
        RowCommand = new AsyncCommand(HandleRowClickAsync, onError: onError);
        DownloadCommand = new AsyncCommand(DownloadAsync, () => !Item.IsFolder, onError);
        TrashCommand = new AsyncCommand(TrashAsync, () => !Item.IsFolder, onError);
        RenameCommand = new AsyncCommand(RenameAsync, onError: onError);
        CopyCommand = new AsyncCommand(CopyAsync, onError: onError);
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

    public AsyncCommand RowCommand { get; }

    public AsyncCommand DownloadCommand { get; }

    public AsyncCommand TrashCommand { get; }

    public AsyncCommand RenameCommand { get; }

    public AsyncCommand CopyCommand { get; }

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
        if (Item.IsFolder)
        {
            return;
        }

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
}
