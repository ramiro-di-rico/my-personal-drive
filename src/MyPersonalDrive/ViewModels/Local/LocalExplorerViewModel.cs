using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;

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
                Items.Add(new LocalNodeViewModel(item, i => NavigateAsync(i.Path), _onError));
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
}
