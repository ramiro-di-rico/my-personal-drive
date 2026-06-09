using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;

namespace MyPersonalDrive.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ProtonDriveService _service;
    private readonly AppSettingsService _settings;
    private readonly Stack<string> _navigationHistory = new();
    private readonly string _rootPath = "/my-files";
    private string _cliPath;
    private string _currentPath = "/my-files";
    private string _statusMessage = "Select a Proton Drive CLI executable to begin.";
    private bool _isLoading;
    private bool _isAuthenticated;
    private string _selectedName = "None";
    private string _selectedKind = "None";
    private string _selectedPath = "None";
    private string _selectedSize = "None";
    private string _selectedModified = "None";
    private string _selectedOwner = "None";
    private string _selectedShared = "None";

    public MainWindowViewModel(ProtonDriveService service, AppSettingsService settings)
    {
        _service = service;
        _settings = settings;

        var appSettings = settings.Load();
        _cliPath = appSettings.CliPath;
        _isAuthenticated = appSettings.IsAuthenticated;

        RootItems = new ObservableCollection<DriveNodeViewModel>();
        BreadcrumbItems = new ObservableCollection<BreadcrumbSegmentViewModel>();
        UpdateBreadcrumbs(_rootPath);

        AuthenticateCommand = new AsyncCommand(AuthenticateAsync, CanAuthenticate);
        LogoutCommand = new AsyncCommand(LogoutAsync, CanLogout);
        RefreshCommand = new AsyncCommand(RefreshAsync, CanRefresh);
        BackCommand = new AsyncCommand(GoBackAsync, CanGoBack);
        UploadCommand = new AsyncCommand(UploadAsync, CanUpload);
    }

    public ObservableCollection<DriveNodeViewModel> RootItems { get; }

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbItems { get; }

    public AsyncCommand AuthenticateCommand { get; }

    public AsyncCommand LogoutCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand BackCommand { get; }

    public AsyncCommand UploadCommand { get; }

    public Func<Task<IReadOnlyList<string>>>? RequestUploadFilesAsync { get; set; }

    public Func<Task<string?>>? RequestDownloadFolderAsync { get; set; }

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
        set => SetProperty(ref _statusMessage, value);
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
            StatusMessage = FormatCliError("auth login", ex.Message);
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
            StatusMessage = FormatCliError("auth logout", ex.Message);
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
            StatusMessage = FormatCliError(previousPath, ex.Message);
        }
    }

    private async Task NavigateIntoAsync(string path)
    {
        if (string.Equals(CurrentPath, path, StringComparison.Ordinal))
        {
            return;
        }

        _navigationHistory.Push(CurrentPath);
        RaiseCommandStates();

        try
        {
            await LoadFolderAsync(path, clearSelection: true);
        }
        catch (InvalidOperationException ex)
        {
            if (_navigationHistory.Count > 0)
            {
                _navigationHistory.Pop();
                RaiseCommandStates();
            }

            StatusMessage = FormatCliError(path, ex.Message);
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
            StatusMessage = FormatCliError(CurrentPath, ex.Message);
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

        try
        {
            IsLoading = true;
            StatusMessage = $"Uploading {files.Count} file(s) to {CurrentPath}...";
            await _service.UploadFilesAsync(files, CurrentPath);
            StatusMessage = $"Uploaded {files.Count} file(s) to {CurrentPath}.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(CurrentPath, ex.Message);
            return;
        }
        finally
        {
            IsLoading = false;
        }

        await RefreshAsync();
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
            StatusMessage = FormatCliError(item.Path, ex.Message);
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
        try
        {
            IsLoading = true;
            StatusMessage = $"Loading {path}...";

            var items = await _service.LoadFolderAsync(path);
            RootItems.Clear();

            foreach (var item in items.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                RootItems.Add(new DriveNodeViewModel(item, HandleRowClickAsync, DownloadItemAsync));
            }

            CurrentPath = path;
            UpdateBreadcrumbs(path);

            if (clearSelection)
            {
                ClearSelection();
            }

            StatusMessage = $"Loaded {RootItems.Count} items from {path}.";
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("login first", StringComparison.OrdinalIgnoreCase))
            {
                IsAuthenticated = false;
            }

            StatusMessage = FormatCliError(path, ex.Message);
            throw;
        }
        finally
        {
            IsLoading = false;
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

            BreadcrumbItems.Add(new BreadcrumbSegmentViewModel(segment, currentPath, currentPath == path, NavigateIntoAsync));
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
    }

    private static string FormatCliError(string path, string message)
    {
        if (message.Contains("login first", StringComparison.OrdinalIgnoreCase))
        {
            return path == "auth login"
                ? "Authentication required. Use Authenticate to sign in."
                : $"Authentication required to load {path}.";
        }

        if (path == "auth logout")
        {
            return $"Logout failed: {message}";
        }

        return $"Failed to load {path}: {message}";
    }
}
