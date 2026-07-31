using MyPersonalDrive.Models;

namespace MyPersonalDrive.ViewModels;

public sealed class DriveNodeViewModel : ObservableObject
{
    private readonly Func<DriveItem, Task> _handleRowClickAsync;
    private readonly Func<DriveItem, Task> _downloadItemAsync;
    private readonly Func<DriveItem, Task> _trashItemAsync;
    private readonly Func<DriveItem, Task> _renameItemAsync;
    private readonly Func<DriveItem, Task> _copyItemAsync;

    public DriveNodeViewModel(DriveItem item, Func<DriveItem, Task> handleRowClickAsync, Func<DriveItem, Task> downloadItemAsync, Func<DriveItem, Task> trashItemAsync, Func<DriveItem, Task> renameItemAsync, Func<DriveItem, Task> copyItemAsync, Action<Exception>? onError = null)
    {
        Item = item;
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

    public string DisplayName => string.IsNullOrWhiteSpace(Item.Name) ? Item.Path : Item.Name;

    public string Icon => Item.IsFolder ? "🗂️" : "📄";

    public string Path => Item.Path;

    public string Kind => Item.IsFolder ? "Folder" : "File";

    public string? SizeText => Item.Size is null ? null : $"{Item.Size:n0} bytes";

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
