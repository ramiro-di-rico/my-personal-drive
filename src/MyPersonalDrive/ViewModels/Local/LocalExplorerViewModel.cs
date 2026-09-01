using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.ViewModels.Local;

/// <summary>
/// The local pane of the dual-pane explorer (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 3) — the
/// filesystem-facing counterpart to the cloud pane already on <c>MainWindowViewModel</c>. Composed
/// into it the same way <c>FolderMetricsViewModel</c>/<c>SyncPanelViewModel</c> already are, rather
/// than folded into the parent VM directly: this is its own state machine (current path, listing,
/// hidden-files preference) with no cloud dependency.
/// </summary>
public sealed class LocalExplorerViewModel : ObservableObject
{
    private readonly LocalFileSystemService _service;
    private readonly AppSettingsService _settings;
    private readonly Action<Exception>? _onError;
    private string _currentPath;
    private bool _showHiddenFiles;
    private string _freeSpaceText = string.Empty;
    private bool _isLoading;
    private string? _statusMessage;

    public LocalExplorerViewModel(LocalFileSystemService service, AppSettingsService settings, Action<Exception>? onError = null)
    {
        _service = service;
        _settings = settings;
        _onError = onError;
        HomePath = service.GetHomeDirectory();
        _currentPath = HomePath;
        _showHiddenFiles = settings.Load().ShowHiddenLocalFiles;
        Items = new ObservableCollection<LocalNodeViewModel>();
        BreadcrumbItems = new ObservableCollection<BreadcrumbSegmentViewModel>();

        RefreshCommand = new AsyncCommand(() => NavigateAsync(_currentPath), () => !IsLoading, onError);
        GoHomeCommand = new AsyncCommand(() => NavigateAsync(HomePath), () => !IsLoading, onError);
        BackCommand = new AsyncCommand(GoBackAsync, () => !IsLoading && CanGoBack, onError);
        ToggleHiddenFilesCommand = new AsyncCommand(ToggleHiddenFilesAsync, () => !IsLoading, onError);
    }

    public ObservableCollection<LocalNodeViewModel> Items { get; }

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbItems { get; }

    public string HomePath { get; }

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    private bool CanGoBack => !PathsEqual(CurrentPath, System.IO.Path.GetPathRoot(CurrentPath) ?? CurrentPath);

    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        private set => SetProperty(ref _showHiddenFiles, value);
    }

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set => SetProperty(ref _freeSpaceText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand GoHomeCommand { get; }

    public AsyncCommand BackCommand { get; }

    public AsyncCommand ToggleHiddenFilesCommand { get; }

    /// <summary>A yes/no confirmation, used before permanently deleting a local item.</summary>
    public Func<string, Task<bool>>? RequestConfirmationAsync { get; set; }

    /// <summary>Prompts for a new name given the current one; null/unchanged means cancelled.</summary>
    public Func<string, Task<string?>>? RequestRenameAsync { get; set; }

    public Func<string, Task>? RequestCopyToClipboardAsync { get; set; }

    public Func<string, IReadOnlyList<PropertyField>, Task>? RequestShowPropertiesAsync { get; set; }

    /// <summary>Opens the "Add sync pair" wizard pre-filled with this row's local path.</summary>
    public Func<string, Task>? RequestSyncSelectedPathAsync { get; set; }

    /// <summary>Looks up the configured sync pair (if any) whose local side is a given path.</summary>
    public Func<string, SyncPairViewModel?>? FindSyncPairByPath { get; set; }

    /// <summary>Best-effort: a home directory that can't be listed shows a status message, not a crash at startup.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await NavigateAsync(HomePath);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }

    public async Task NavigateAsync(string path)
    {
        IsLoading = true;
        StatusMessage = null;

        try
        {
            var items = await Task.Run(() => _service.ListDirectory(path, ShowHiddenFiles));
            var sorted = DriveItemSorter.Sort(items, DriveSortKey.Name, descending: false);

            CurrentPath = path;
            Items.Clear();
            foreach (var item in sorted)
            {
                Items.Add(new LocalNodeViewModel(item, i => NavigateAsync(i.Path), _onError, new LocalNodeSyncActions
                {
                    FindSyncPair = i => FindSyncPairByPath?.Invoke(i.Path),
                    SyncSelectedPathAsync = SyncSelectedPathAsync,
                    CopyPathAsync = CopyPathAsync,
                    RenameAsync = RenameItemAsync,
                    DeleteAsync = DeleteItemAsync,
                    ShowPropertiesAsync = ShowPropertiesAsync,
                    RefreshPaneAsync = () => NavigateAsync(CurrentPath),
                }));
            }

            RebuildBreadcrumbs();
            FreeSpaceText = _service.AvailableFreeBytes(path) is { } bytes ? ByteSize.Format(bytes) : "—";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            StatusMessage = $"Can't open '{path}': {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task GoBackAsync()
    {
        var parent = System.IO.Path.GetDirectoryName(CurrentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(parent) ? Task.CompletedTask : NavigateAsync(parent);
    }

    private Task ToggleHiddenFilesAsync()
    {
        ShowHiddenFiles = !ShowHiddenFiles;
        _settings.Update(s => s.ShowHiddenLocalFiles = ShowHiddenFiles);
        return NavigateAsync(CurrentPath);
    }

    private void RebuildBreadcrumbs()
    {
        BreadcrumbItems.Clear();

        var root = System.IO.Path.GetPathRoot(CurrentPath) ?? CurrentPath;
        var segments = new List<(string Label, string Path)> { (root, root) };

        var relative = CurrentPath[root.Length..].Trim(System.IO.Path.DirectorySeparatorChar);
        if (!string.IsNullOrEmpty(relative))
        {
            // Combine from the untouched root, not a trimmed copy: trimming "/" itself down to ""
            // would turn every combine below into a relative path ("tmp" instead of "/tmp").
            var accumulated = root;
            foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = System.IO.Path.Combine(accumulated, part);
                segments.Add((part, accumulated));
            }
        }

        foreach (var (label, path) in segments)
        {
            var isCurrent = PathsEqual(path, CurrentPath);
            BreadcrumbItems.Add(new BreadcrumbSegmentViewModel(label, path, isCurrent, NavigateAsync, _onError));
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a.TrimEnd(System.IO.Path.DirectorySeparatorChar), b.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.Ordinal);

    /// <summary>Permanently deletes a local file/folder — there is no local trash to fall back into, so this always confirms first.</summary>
    public async Task DeleteItemAsync(DriveItem item)
    {
        var confirm = RequestConfirmationAsync;
        var question = item.IsFolder
            ? $"Delete the folder '{item.Name}' and everything inside it? This cannot be undone."
            : $"Delete '{item.Name}'? This cannot be undone.";

        if (confirm is not null && !await confirm(question))
        {
            StatusMessage = $"Cancelled: {item.Name} was not deleted.";
            return;
        }

        try
        {
            _service.Delete(item.Path);
            StatusMessage = $"Deleted {item.Name}.";
            await NavigateAsync(CurrentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not delete '{item.Name}': {ex.Message}";
        }
    }

    public async Task RenameItemAsync(DriveItem item)
    {
        var requester = RequestRenameAsync;
        if (requester is null)
        {
            StatusMessage = "Rename is not available.";
            return;
        }

        var newName = await requester(item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
        {
            return;
        }

        try
        {
            _service.Rename(item.Path, newName);
            StatusMessage = $"Renamed {item.Name} to {newName}.";
            await NavigateAsync(CurrentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not rename '{item.Name}': {ex.Message}";
        }
    }

    public async Task CopyPathAsync(DriveItem item)
    {
        var copy = RequestCopyToClipboardAsync;
        if (copy is null)
        {
            StatusMessage = "Copy is not available.";
            return;
        }

        await copy(item.Path);
        StatusMessage = $"Copied path: {item.Path}";
    }

    public async Task SyncSelectedPathAsync(DriveItem item)
    {
        if (!item.IsFolder)
        {
            return;
        }

        var handler = RequestSyncSelectedPathAsync;
        if (handler is null)
        {
            StatusMessage = "Sync is not available.";
            return;
        }

        await handler(item.Path);
    }

    public async Task ShowPropertiesAsync(DriveItem item)
    {
        var show = RequestShowPropertiesAsync;
        if (show is null)
        {
            return;
        }

        var fields = new List<PropertyField>
        {
            new("Name", item.Name),
            new("Path", item.Path),
            new("Type", item.IsFolder ? "Folder" : "File"),
        };

        if (item.Size is not null)
        {
            fields.Add(new PropertyField("Size", ByteSize.Format(item.Size.Value)));
        }

        if (item.ModifiedAt is not null)
        {
            fields.Add(new PropertyField("Modified", item.ModifiedAt.Value.ToLocalTime().ToString("g")));
        }

        await show(item.Name, fields);
    }
}
