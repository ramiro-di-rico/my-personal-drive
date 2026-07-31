using System.Collections.ObjectModel;
using System.IO;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Avalonia.Threading;

namespace MyPersonalDrive.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ProtonDriveService _service;
    private readonly DriveCacheService _cacheService;
    private readonly AppSettingsService _settings;
    private readonly Stack<string> _navigationHistory = new();
    private CancellationTokenSource? _cts;
    private readonly string _rootPath = "/my-files";
    private const int MaxCommandLogLines = 200;
    private readonly List<string> _commandLogLines = new();
    private string _cliPath;
    private string _currentPath = "/my-files";
    private string _statusMessage = "Select a Proton Drive CLI executable to begin.";
    private bool _isWarning;
    private bool _isLoading;
    private bool _isAuthenticated;
    private bool _isCommandConsoleVisible = true;
    private double _commandConsoleMaxHeight = 180;
    private double _commandConsoleOpacity = 1;
    private bool _commandConsoleHitTestVisible = true;
    private string _activeCommand = "Idle";
    private string _commandLogText = "No CLI command running.";
    private string _commandConsoleToggleLabel = "Hide CLI activity";
    private string _commandConsoleToggleGlyph = "▼";
    private string _selectedName = "None";
    private string _selectedKind = "None";
    private string _selectedPath = "None";
    private string _selectedSize = "None";
    private string _selectedModified = "None";
    private string _selectedOwner = "None";
    private string _selectedShared = "None";

    public MainWindowViewModel(ProtonDriveService service, DriveCacheService cacheService, AppSettingsService settings)
    {
        _service = service;
        _cacheService = cacheService;
        _settings = settings;

        var appSettings = settings.Load();
        _cliPath = appSettings.CliPath;
        _isAuthenticated = appSettings.IsAuthenticated;
        _service.CommandStarted += OnCommandStarted;
        _service.CommandOutput += OnCommandOutput;
        _service.CommandFinished += OnCommandFinished;
        _service.ListingParseWarning += OnListingParseWarning;

        RootItems = new ObservableCollection<DriveNodeViewModel>();
        BreadcrumbItems = new ObservableCollection<BreadcrumbSegmentViewModel>();
        UpdateBreadcrumbs(_rootPath);

        AuthenticateCommand = new AsyncCommand(AuthenticateAsync, CanAuthenticate, HandleUnexpectedError);
        LogoutCommand = new AsyncCommand(LogoutAsync, CanLogout, HandleUnexpectedError);
        RefreshCommand = new AsyncCommand(RefreshAsync, CanRefresh, HandleUnexpectedError);
        BackCommand = new AsyncCommand(GoBackAsync, CanGoBack, HandleUnexpectedError);
        UploadCommand = new AsyncCommand(UploadAsync, CanUpload, HandleUnexpectedError);
        CreateFolderCommand = new AsyncCommand(CreateFolderAsync, CanCreateFolder, HandleUnexpectedError);
        ToggleCommandConsoleCommand = new AsyncCommand(ToggleCommandConsoleAsync, onError: HandleUnexpectedError);
        DownloadActivityCommand = new AsyncCommand(DownloadActivityAsync, CanDownloadActivity, HandleUnexpectedError);
        ClearActivityCommand = new AsyncCommand(ClearActivityAsync, CanClearActivity, HandleUnexpectedError);
    }

    public ObservableCollection<DriveNodeViewModel> RootItems { get; }

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbItems { get; }

    public AsyncCommand AuthenticateCommand { get; }

    public AsyncCommand LogoutCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand BackCommand { get; }

    public AsyncCommand UploadCommand { get; }

    public AsyncCommand CreateFolderCommand { get; }

    public AsyncCommand ToggleCommandConsoleCommand { get; }

    public AsyncCommand DownloadActivityCommand { get; }

    public AsyncCommand ClearActivityCommand { get; }

    public Func<Task<IReadOnlyList<string>>>? RequestUploadFilesAsync { get; set; }

    public Func<IReadOnlyList<string>, Task<UploadConflictStrategy>>? RequestConflictStrategyAsync { get; set; }

    public Func<string, Task<string?>>? RequestRenameAsync { get; set; }

    public Func<string, Task<string?>>? RequestCopyNameAsync { get; set; }

    public Func<Task<string?>>? RequestCreateFolderAsync { get; set; }

    public Func<Task<string?>>? RequestDownloadFolderAsync { get; set; }

    public Func<Task<string?>>? RequestSaveActivityAsync { get; set; }

    public string CliPath
    {
        get => _cliPath;
        set
        {
            if (SetProperty(ref _cliPath, value))
            {
                PersistSettings();
                RaiseCommandStates();
            }
        }
    }

    public string RootPath => _rootPath;

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (SetProperty(ref _isAuthenticated, value))
            {
                PersistSettings();
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                IsWarning = false;
            }
        }
    }

    public bool IsWarning
    {
        get => _isWarning;
        private set => SetProperty(ref _isWarning, value);
    }

    public string ActiveCommand
    {
        get => _activeCommand;
        private set => SetProperty(ref _activeCommand, value);
    }

    public string CommandLogText
    {
        get => _commandLogText;
        private set => SetProperty(ref _commandLogText, value);
    }

    public string SelectedName
    {
        get => _selectedName;
        private set => SetProperty(ref _selectedName, value);
    }

    public string SelectedKind
    {
        get => _selectedKind;
        private set => SetProperty(ref _selectedKind, value);
    }

    public string SelectedPath
    {
        get => _selectedPath;
        private set => SetProperty(ref _selectedPath, value);
    }

    public string SelectedSize
    {
        get => _selectedSize;
        private set => SetProperty(ref _selectedSize, value);
    }

    public string SelectedModified
    {
        get => _selectedModified;
        private set => SetProperty(ref _selectedModified, value);
    }

    public string SelectedOwner
    {
        get => _selectedOwner;
        private set => SetProperty(ref _selectedOwner, value);
    }

    public string SelectedShared
    {
        get => _selectedShared;
        private set => SetProperty(ref _selectedShared, value);
    }

    public bool IsCommandConsoleVisible
    {
        get => _isCommandConsoleVisible;
        private set
        {
            if (SetProperty(ref _isCommandConsoleVisible, value))
            {
                CommandConsoleMaxHeight = value ? 180 : 0;
                CommandConsoleOpacity = value ? 1 : 0;
                CommandConsoleHitTestVisible = value;
                CommandConsoleToggleLabel = value ? "Hide CLI activity" : "Show CLI activity";
                CommandConsoleToggleGlyph = value ? "▼" : "▲";
                RaiseCommandStates();
            }
        }
    }

    public double CommandConsoleMaxHeight
    {
        get => _commandConsoleMaxHeight;
        private set => SetProperty(ref _commandConsoleMaxHeight, value);
    }

    public double CommandConsoleOpacity
    {
        get => _commandConsoleOpacity;
        private set => SetProperty(ref _commandConsoleOpacity, value);
    }

    public bool CommandConsoleHitTestVisible
    {
        get => _commandConsoleHitTestVisible;
        private set => SetProperty(ref _commandConsoleHitTestVisible, value);
    }

    public string CommandConsoleToggleLabel
    {
        get => _commandConsoleToggleLabel;
        private set => SetProperty(ref _commandConsoleToggleLabel, value);
    }

    public string CommandConsoleToggleGlyph
    {
        get => _commandConsoleToggleGlyph;
        private set => SetProperty(ref _commandConsoleToggleGlyph, value);
    }

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(CliPath) && IsAuthenticated)
        {
            await GoToRootAsync();
            return;
        }

        StatusMessage = string.IsNullOrWhiteSpace(CliPath)
            ? "Select a Proton Drive CLI executable to begin."
            : "Authenticate to load /my-files.";
    }

    private bool CanAuthenticate() => !IsLoading && !IsAuthenticated && !string.IsNullOrWhiteSpace(CliPath);

    private bool CanLogout() => !IsLoading && IsAuthenticated;

    private bool CanRefresh() => !IsLoading && IsAuthenticated;

    private bool CanGoBack() => !IsLoading && IsAuthenticated && _navigationHistory.Count > 0;

    private bool CanUpload() => !IsLoading && IsAuthenticated;

    private bool CanCreateFolder() => !IsLoading && IsAuthenticated;

    private bool CanDownloadActivity() => _commandLogLines.Count > 0;

    private bool CanClearActivity() => _commandLogLines.Count > 0;

    private async Task AuthenticateAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Opening Proton Drive authentication in your browser...";
            await _service.AuthenticateAsync();
            IsAuthenticated = true;
            await GoToRootAsync();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError("auth login", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LogoutAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Logging out from Proton Drive...";
            await _service.LogoutAsync();
            IsAuthenticated = false;
            ResetBrowserState();
            StatusMessage = "Logged out.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError("auth logout", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task GoToRootAsync()
    {
        _navigationHistory.Clear();
        await LoadFolderAsync(RootPath, clearSelection: true);
        RaiseCommandStates();
    }

    private async Task GoBackAsync()
    {
        if (_navigationHistory.Count == 0)
        {
            return;
        }

        var previousPath = _navigationHistory.Pop();
        RaiseCommandStates();

        try
        {
            await LoadFolderAsync(previousPath, clearSelection: true);
        }
        catch (InvalidOperationException ex)
        {
            _navigationHistory.Push(previousPath);
            RaiseCommandStates();
            StatusMessage = FormatCliError(previousPath, ex);
        }
    }

    private async Task NavigateIntoAsync(string path)
    {
        if (string.Equals(CurrentPath, path, StringComparison.Ordinal))
        {
            return;
        }

        var previousPath = CurrentPath;
        _navigationHistory.Push(previousPath);
        RaiseCommandStates();

        try
        {
            await LoadFolderAsync(path, clearSelection: true);
        }
        catch (InvalidOperationException ex)
        {
            if (CurrentPath == path)
            {
                CurrentPath = previousPath;
                UpdateBreadcrumbs(previousPath);
            }

            if (_navigationHistory.Count > 0 && _navigationHistory.Peek() == previousPath)
            {
                _navigationHistory.Pop();
                RaiseCommandStates();
            }

            StatusMessage = FormatCliError(path, ex);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await LoadFolderAsync(CurrentPath, clearSelection: false);
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(CurrentPath, ex);
        }
    }

    private async Task UploadAsync()
    {
        var picker = RequestUploadFilesAsync;
        if (picker is null)
        {
            StatusMessage = "Upload is not available.";
            return;
        }

        var files = await picker();
        if (files.Count == 0)
        {
            return;
        }

        var strategy = UploadConflictStrategy.None;
        if (RequestConflictStrategyAsync is not null)
        {
            var remoteFileNames = RootItems.Select(ni => ni.Item.Name).ToHashSet();
            var conflictingFiles = files
                .Select(Path.GetFileName)
                .Where(name => name is not null && remoteFileNames.Contains(name))
                .ToList();

            if (conflictingFiles.Count > 0)
            {
                strategy = await RequestConflictStrategyAsync(conflictingFiles!);
                if (strategy == UploadConflictStrategy.None)
                {
                    StatusMessage = "Upload cancelled.";
                    return;
                }
            }
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"Uploading {files.Count} file(s) to {CurrentPath}...";
            await _service.UploadFilesAsync(files, CurrentPath, strategy);
            StatusMessage = $"Uploaded {files.Count} file(s) to {CurrentPath}.";

            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(CurrentPath, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CreateFolderAsync()
    {
        var requester = RequestCreateFolderAsync;
        if (requester is null)
        {
            StatusMessage = "Create folder is not available.";
            return;
        }

        var folderName = await requester();
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"Creating folder '{folderName}' in {CurrentPath}...";
            await _service.CreateFolderAsync(CurrentPath, folderName);
            StatusMessage = $"Created folder '{folderName}' in {CurrentPath}.";
            
            // Update DB immediately
            var newFolderPath = ProtonDriveService.CombinePath(CurrentPath, folderName);
            await _cacheService.AddOrUpdateItemAsync(CurrentPath, new DriveItem(newFolderPath, folderName, true));

            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(CurrentPath, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task DownloadItemAsync(DriveItem item)
    {
        var picker = RequestDownloadFolderAsync;
        if (picker is null)
        {
            StatusMessage = "Download is not available.";
            return;
        }

        var folder = await picker();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"Downloading {item.Name}...";
            await _service.DownloadFileAsync(item.Path, folder);
            StatusMessage = $"Downloaded {item.Name} to {folder}.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(item.Path, ex);
        }
        finally
        {
            IsLoading = false;
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
            IsLoading = true;
            StatusMessage = $"Renaming {item.Name} to {newName}...";
            await _service.RenameItemAsync(item.Path, newName);
            StatusMessage = $"Renamed {item.Name} to {newName}.";

            // Update DB immediately
            var parentPath = GetParentPath(item.Path);
            var newPath = ProtonDriveService.CombinePath(parentPath, newName);
            await _cacheService.RemoveItemAsync(item.Path);
            await _cacheService.AddOrUpdateItemAsync(parentPath, item with { Path = newPath, Name = newName });

            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(item.Path, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CopyItemAsync(DriveItem item)
    {
        var requester = RequestCopyNameAsync;
        if (requester is null)
        {
            StatusMessage = "Copy is not available.";
            return;
        }

        var newName = await requester(item.Name);
        if (newName == null)
        {
            return;
        }

        var displayTarget = string.IsNullOrEmpty(newName) ? item.Name : newName;

        try
        {
            IsLoading = true;
            StatusMessage = $"Creating a copy of {item.Name} as {displayTarget} in {CurrentPath}...";
            await _service.CopyItemAsync(item.Path, CurrentPath, string.IsNullOrEmpty(newName) ? null : newName);
            StatusMessage = $"Copied {item.Name} successfully.";
            
            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(item.Path, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task TrashItemAsync(DriveItem item)
    {
        if (item.IsFolder)
        {
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"Moving {item.Name} to trash...";
            await _service.TrashItemAsync(item.Path);
            StatusMessage = $"Moved {item.Name} to trash.";

            // Update DB immediately
            await _cacheService.RemoveItemAsync(item.Path);
            
            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(item.Path, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task HandleRowClickAsync(DriveItem item)
    {
        SelectItem(item);

        if (!item.IsFolder)
        {
            return;
        }

        await NavigateIntoAsync(item.Path);
    }

    private async Task LoadFolderAsync(string path, bool clearSelection)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = $"Loading {path}...";

            // Always update current path and breadcrumbs immediately to show transition
            var previousPath = CurrentPath;
            CurrentPath = path;
            UpdateBreadcrumbs(path);
            
            if (clearSelection)
            {
                ClearSelection();
            }

            // 1. Load from DB
            var cachedItems = await _cacheService.GetCachedItemsAsync(path);
            bool hasCache = cachedItems.Count > 0;
            
            if (hasCache)
            {
                DisplayItems(cachedItems);
                StatusMessage = $"Showing cached items for {path}. Fetching latest from CLI...";
                IsLoading = false;

                // Fire and forget CLI fetch to keep UI responsive and command finished
                _ = FetchFromCliAndUpdateCacheAsync(path, clearSelection, token);
                return;
            }
            else
            {
                // Clear items while waiting for CLI
                DisplayItems(Array.Empty<DriveItem>());
                await FetchFromCliAndUpdateCacheAsync(path, clearSelection, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (InvalidOperationException ex)
        {
            HandleLoadError(path, ex);
        }
        finally
        {
            if (CurrentPath == path && !token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private async Task FetchFromCliAndUpdateCacheAsync(string path, bool clearSelection, CancellationToken token)
    {
        try
        {
            // 2. Fetch from CLI
            var items = await _service.LoadFolderAsync(path, token);

            // 3. Update DB
            await _cacheService.SyncItemsAsync(path, items);

            // 4. Update UI if we are still on the same path
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CurrentPath == path && !token.IsCancellationRequested)
                {
                    DisplayItems(items);
                    UpdateBreadcrumbs(path);

                    if (clearSelection)
                    {
                        ClearSelection();
                    }

                    StatusMessage = $"Loaded {RootItems.Count} items from {path}.";
                    IsLoading = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (InvalidOperationException ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => HandleLoadError(path, ex));
        }
    }

    private void HandleLoadError(string path, InvalidOperationException ex)
    {
        var kind = (ex as CliException)?.Kind ?? CliErrorKind.Unknown;

        if (kind == CliErrorKind.NotFound)
        {
            StatusMessage = $"Warning: The path '{path}' no longer exists.";
            IsWarning = true;
            return;
        }

        if (kind == CliErrorKind.NotAuthenticated)
        {
            IsAuthenticated = false;
        }

        StatusMessage = FormatCliError(path, ex);
        IsWarning = true;
    }

    private void DisplayItems(IEnumerable<DriveItem> items)
    {
        RootItems.Clear();
        foreach (var item in items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            RootItems.Add(new DriveNodeViewModel(item, HandleRowClickAsync, DownloadItemAsync, TrashItemAsync, RenameItemAsync, CopyItemAsync, HandleUnexpectedError));
        }
    }

    private void UpdateBreadcrumbs(string path)
    {
        BreadcrumbItems.Clear();

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = string.Empty;

        foreach (var segment in segments)
        {
            currentPath = string.IsNullOrEmpty(currentPath)
                ? "/" + segment
                : currentPath + "/" + segment;

            BreadcrumbItems.Add(new BreadcrumbSegmentViewModel(segment, currentPath, currentPath == path, NavigateIntoAsync, HandleUnexpectedError));
        }
    }

    private void SelectItem(DriveItem item)
    {
        SelectedName = item.Name;
        SelectedKind = item.IsFolder ? "Folder" : "File";
        SelectedPath = item.Path;
        SelectedSize = item.Size is null ? "None" : $"{item.Size:n0} bytes";
        SelectedModified = item.ModifiedAt ?? "None";
        SelectedOwner = item.Owner ?? "None";
        SelectedShared = item.IsShared ? "Yes" : "No";
        StatusMessage = $"Selected {item.Name}.";
    }

    private void ClearSelection()
    {
        SelectedName = "None";
        SelectedKind = "None";
        SelectedPath = "None";
        SelectedSize = "None";
        SelectedModified = "None";
        SelectedOwner = "None";
        SelectedShared = "None";
    }

    private void ResetBrowserState()
    {
        _navigationHistory.Clear();
        RootItems.Clear();
        UpdateBreadcrumbs(_rootPath);
        CurrentPath = _rootPath;
        ClearSelection();
    }

    private void PersistSettings()
    {
        _settings.Save(new AppSettings
        {
            CliPath = CliPath,
            IsAuthenticated = IsAuthenticated
        });
    }

    private void RaiseCommandStates()
    {
        AuthenticateCommand.RaiseCanExecuteChanged();
        LogoutCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        UploadCommand.RaiseCanExecuteChanged();
        CreateFolderCommand.RaiseCanExecuteChanged();
        DownloadActivityCommand.RaiseCanExecuteChanged();
        ClearActivityCommand.RaiseCanExecuteChanged();
    }

    private async Task ToggleCommandConsoleAsync()
    {
        IsCommandConsoleVisible = !IsCommandConsoleVisible;
        await Task.CompletedTask;
    }

    private async Task DownloadActivityAsync()
    {
        var picker = RequestSaveActivityAsync;
        if (picker is null)
        {
            StatusMessage = "Activity export is not available.";
            return;
        }

        var path = await picker();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, CommandLogText);
            StatusMessage = $"Saved CLI activity to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Failed to save CLI activity to {path}: {ex.Message}";
            IsWarning = true;
        }
    }

    private async Task ClearActivityAsync()
    {
        _commandLogLines.Clear();
        CommandLogText = "No CLI command running.";
        ActiveCommand = "Idle";
        RaiseCommandStates();
        await Task.CompletedTask;
    }

    private void OnCommandStarted(object? sender, CliCommandStartedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            ActiveCommand = e.CommandText;
            AppendCommandLine($"> {e.CommandText}");
        });

    private void OnCommandOutput(object? sender, CliCommandOutputEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            AppendCommandLine(e.IsError ? $"[err] {e.Text}" : e.Text);
        });

    private void OnCommandFinished(object? sender, CliCommandFinishedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            AppendCommandLine(e.Succeeded
                ? $"[done] exit {e.ExitCode}"
                : $"[fail] exit {e.ExitCode}");
            ActiveCommand = "Idle";
        });

    private void OnListingParseWarning(object? sender, string message)
        => Dispatcher.UIThread.Post(() => AppendCommandLine($"[warn] {message}"));

    private void AppendCommandLine(string line)
    {
        _commandLogLines.Add(line);
        if (_commandLogLines.Count > MaxCommandLogLines)
        {
            _commandLogLines.RemoveAt(0);
        }

        CommandLogText = string.Join(Environment.NewLine, _commandLogLines);
        RaiseCommandStates();
    }

    /// <summary>
    /// Catch-all for exceptions that escape a command's Func&lt;Task&gt; and are not the
    /// expected InvalidOperationException the CLI layer throws. Without this, AsyncCommand's
    /// async void Execute would let the exception terminate the process.
    /// </summary>
    private void HandleUnexpectedError(Exception ex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CrashLog.Write(ex);
            StatusMessage = $"Unexpected error: {ex.Message}";
            IsWarning = true;
            AppendCommandLine($"[err] Unexpected error: {ex}");
            IsLoading = false;
        });
    }

    private static string FormatCliError(string path, Exception ex)
    {
        var kind = (ex as CliException)?.Kind ?? CliErrorKind.Unknown;

        if (kind == CliErrorKind.NotAuthenticated)
        {
            return path == "auth login"
                ? "Authentication required. Use Authenticate to sign in."
                : $"Authentication required to load {path}.";
        }

        if (path == "auth logout")
        {
            return $"Logout failed: {ex.Message}";
        }

        return $"Failed to load {path}: {ex.Message}";
    }

    private static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return "/";
        }

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return "/";
        }

        return path[..lastSlash];
    }
}
