using MyPersonalDrive.Models;

namespace MyPersonalDrive.ViewModels;

public sealed class DriveNodeViewModel : ObservableObject
{
    private readonly Func<DriveItem, Task> _handleRowClickAsync;
    private readonly Func<DriveItem, Task> _downloadItemAsync;
    private readonly Func<DriveItem, Task> _trashItemAsync;

    public DriveNodeViewModel(DriveItem item, Func<DriveItem, Task> handleRowClickAsync, Func<DriveItem, Task> downloadItemAsync, Func<DriveItem, Task> trashItemAsync)
    {
        Item = item;
        _handleRowClickAsync = handleRowClickAsync;
        _downloadItemAsync = downloadItemAsync;
        _trashItemAsync = trashItemAsync;
        RowCommand = new AsyncCommand(HandleRowClickAsync);
        DownloadCommand = new AsyncCommand(DownloadAsync, () => !Item.IsFolder);
        TrashCommand = new AsyncCommand(TrashAsync, () => !Item.IsFolder);
    }

    public DriveItem Item { get; }

    public bool IsFolder => Item.IsFolder;

    public bool IsFile => !Item.IsFolder;

    public string DisplayName => string.IsNullOrWhiteSpace(Item.Name) ? Item.Path : Item.Name;

    public string Icon => Item.IsFolder ? "🗂️" : "📄";

    public string Path => Item.Path;

    public string Kind => Item.IsFolder ? "Folder" : "File";

    public string? SizeText => Item.Size is null ? null : $"{Item.Size:n0} bytes";

    public string? ModifiedText => Item.ModifiedAt;

    public string? OwnerText => Item.Owner;

    public bool SharedText => Item.IsShared;

    public AsyncCommand RowCommand { get; }

    public AsyncCommand DownloadCommand { get; }

    public AsyncCommand TrashCommand { get; }

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
}
