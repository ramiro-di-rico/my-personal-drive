using MyPersonalDrive.Models;

namespace MyPersonalDrive.ViewModels;

public sealed class DriveNodeViewModel : ObservableObject
{
    private readonly Func<DriveItem, Task> _handleRowClickAsync;
    private readonly Func<DriveItem, Task> _downloadItemAsync;

    public DriveNodeViewModel(DriveItem item, Func<DriveItem, Task> handleRowClickAsync, Func<DriveItem, Task> downloadItemAsync)
    {
        Item = item;
        _handleRowClickAsync = handleRowClickAsync;
        _downloadItemAsync = downloadItemAsync;
        RowCommand = new AsyncCommand(HandleRowClickAsync);
        DownloadCommand = new AsyncCommand(DownloadAsync, () => !Item.IsFolder);
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
}
