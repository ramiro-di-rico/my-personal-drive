using System.Collections.ObjectModel;
using System.Data.Common;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using Avalonia.Threading;

namespace MyPersonalDrive.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ICloudDriveProvider _provider;
    private readonly DriveCacheService _cacheService;
    private readonly AppSettingsService _settings;
    private readonly FolderMetricsStore? _metricsStore;
    private readonly FolderStatsScanner? _statsScanner;
    private CancellationTokenSource? _deepScanCts;
    private readonly Stack<string> _navigationHistory = new();
    private readonly TimeProvider _timeProvider;
    private readonly RemoteViewFreshnessPolicy _remoteViewFreshness = new();
    private CancellationTokenSource? _cts;
    private DriveNodeViewModel? _selectedNode;
    private readonly string _rootPath = "/my-files";
    private readonly CommandLogBuffer _commandLog = new();

    /// <summary>
    /// Guards <see cref="_pendingCommandLines"/>, which the CLI executor's events fill from whatever
    /// thread the process I/O happened on — and now from up to eight of them at once, since read-only
    /// commands run concurrently.
    /// </summary>
    private readonly object _commandLogGate = new();
    private readonly List<string> _pendingCommandLines = new();
    private bool _commandLogFlushScheduled;
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
    private bool _hasSelection;
    private bool _isSettingsView;
    private DriveViewMode _viewMode = DriveViewMode.List;
    private bool _isDeepScanRunning;
    private DriveSortKey _sortKey = DriveSortKey.Name;
    /// <summary>
    /// Everything the current folder holds, before filtering. The rows in <see cref="RootItems"/> are
    /// a view of this: a filter must never make the app forget what it loaded, or clearing it would
    /// need another CLI call.
    /// </summary>
    private IReadOnlyList<DriveItem> _loadedItems = [];
    private FileKind? _kindFilter;
    private string _filterSummary = string.Empty;
    private bool _sortDescending;
    private const string UnknownCliVersion = "Unknown";
    private string _cliVersion = UnknownCliVersion;
    private bool _isCheckingCliVersion;
    private readonly ICliReleaseFeed? _releaseFeed;
    private readonly CliUpdateInstaller _updateInstaller;
    private readonly Func<bool> _isSyncInProgress;
    private CliReleaseCandidate? _availableRelease;
    private string _cliUpdateStatus = "Not checked yet.";
    private bool _isCliUpdateAvailable;
    private bool _isCliUpdateBusy;

    public MainWindowViewModel(
        ICloudDriveProvider provider,
        DriveCacheService cacheService,
        AppSettingsService settings,
        Sync.SyncPanelViewModel syncPanel,
        TimeProvider? timeProvider = null,
        ICliReleaseFeed? releaseFeed = null,
        CliUpdateInstaller? updateInstaller = null,
        Func<bool>? isSyncInProgress = null,
        FolderMetricsStore? metricsStore = null,
        FolderStatsScanner? statsScanner = null)
    {
        // Optional so the many existing view-model tests don't all have to build a database and a
        // scanner to exercise unrelated behavior. When either is absent the deep-scan command
        // simply can't execute (see CanScanFolderDeeply), which is also the honest state of the
        // feature on a machine with no CLI configured.
        _metricsStore = metricsStore;
        _statsScanner = statsScanner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _releaseFeed = releaseFeed;
        _updateInstaller = updateInstaller ?? new CliUpdateInstaller();
        // Injected rather than read off SyncPanel directly so the refusal-while-syncing path is
        // reachable in a test without driving a real sync cycle to a chosen moment.
        // Capturing the parameter, not the SyncPanel property, which is only assigned below.
        _isSyncInProgress = isSyncInProgress ?? (() => syncPanel.IsSyncInProgress);
        _provider = provider;
        _cacheService = cacheService;
        _settings = settings;
        SyncPanel = syncPanel;

        var appSettings = settings.Load();
        _cliPath = appSettings.CliPath;
        _isAuthenticated = appSettings.IsAuthenticated;
        // Set through the field, not the property: the property persists, and the constructor has
        // no business writing settings.json back on every launch.
        _viewMode = appSettings.ViewModeOrDefault();
        _sortKey = appSettings.SortKeyOrDefault();
        _sortDescending = appSettings.SortDescending;
        _provider.CommandStarted += OnCommandStarted;
        _provider.CommandOutput += OnCommandOutput;
        _provider.CommandFinished += OnCommandFinished;
        _provider.ListingParseWarning += OnListingParseWarning;

        RootItems = new ObservableCollection<DriveNodeViewModel>();
        // Selecting a "largest item" row must behave exactly like clicking that row in the listing,
        // so it goes through the same handler rather than a second selection path.
        Metrics = new FolderMetricsViewModel(HandleRowClickAsync, HandleUnexpectedError);
        KindFilters = new ObservableCollection<KindFilterViewModel>();
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
        ShowExplorerCommand = new AsyncCommand(ShowExplorerAsync, onError: HandleUnexpectedError);
        SortByNameCommand = new AsyncCommand(() => SortByAsync(DriveSortKey.Name), onError: HandleUnexpectedError);
        SortBySizeCommand = new AsyncCommand(() => SortByAsync(DriveSortKey.Size), onError: HandleUnexpectedError);
        SortByModifiedCommand = new AsyncCommand(() => SortByAsync(DriveSortKey.Modified), onError: HandleUnexpectedError);
        SortByKindCommand = new AsyncCommand(() => SortByAsync(DriveSortKey.Kind), onError: HandleUnexpectedError);
        ScanFolderDeeplyCommand = new AsyncCommand(ScanFolderDeeplyAsync, CanScanFolderDeeply, HandleUnexpectedError);
        CancelDeepScanCommand = new AsyncCommand(CancelDeepScanAsync, () => IsDeepScanRunning, HandleUnexpectedError);
        ShowListViewCommand = new AsyncCommand(() => SetViewModeAsync(DriveViewMode.List), onError: HandleUnexpectedError);
        ShowIconsViewCommand = new AsyncCommand(() => SetViewModeAsync(DriveViewMode.Icons), onError: HandleUnexpectedError);
        ShowGalleryViewCommand = new AsyncCommand(() => SetViewModeAsync(DriveViewMode.Gallery), onError: HandleUnexpectedError);
        ShowSettingsCommand = new AsyncCommand(ShowSettingsAsync, onError: HandleUnexpectedError);
        CheckCliVersionCommand = new AsyncCommand(CheckCliVersionAsync, CanCheckCliVersion, HandleUnexpectedError);
        CheckForCliUpdateCommand = new AsyncCommand(CheckForCliUpdateAsync, CanCheckForCliUpdate, HandleUnexpectedError);
        InstallCliUpdateCommand = new AsyncCommand(InstallCliUpdateAsync, CanInstallCliUpdate, HandleUnexpectedError);
    }

    public ObservableCollection<DriveNodeViewModel> RootItems { get; }

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbItems { get; }

    /// <summary>
    /// The type filters worth offering for the folder on screen — one per kind actually present,
    /// plus "Todos". Rebuilt with the listing (docs/PLAN-BROWSER-VIEWS.md M6).
    /// </summary>
    public ObservableCollection<KindFilterViewModel> KindFilters { get; }

    public Sync.SyncPanelViewModel SyncPanel { get; }

    /// <summary>
    /// Statistics for the folder on screen, recomputed from the listing on every load
    /// (docs/PLAN-BROWSER-VIEWS.md M2). Shallow only: direct children, no CLI calls.
    /// </summary>
    public FolderMetricsViewModel Metrics { get; }

    public AsyncCommand AuthenticateCommand { get; }

    public AsyncCommand LogoutCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand BackCommand { get; }

    public AsyncCommand SortByNameCommand { get; }

    public AsyncCommand SortBySizeCommand { get; }

    public AsyncCommand SortByModifiedCommand { get; }

    public AsyncCommand SortByKindCommand { get; }

    public AsyncCommand ScanFolderDeeplyCommand { get; }

    public AsyncCommand CancelDeepScanCommand { get; }

    public AsyncCommand ShowListViewCommand { get; }

    public AsyncCommand ShowIconsViewCommand { get; }

    public AsyncCommand ShowGalleryViewCommand { get; }

    public AsyncCommand UploadCommand { get; }

    public AsyncCommand CreateFolderCommand { get; }

    public AsyncCommand ToggleCommandConsoleCommand { get; }

    public AsyncCommand DownloadActivityCommand { get; }

    public AsyncCommand ClearActivityCommand { get; }

    public AsyncCommand ShowExplorerCommand { get; }

    public AsyncCommand ShowSettingsCommand { get; }

    public AsyncCommand CheckCliVersionCommand { get; }

    /// <summary>
    /// What `proton-drive --version` last reported, or why it could not be read. Shown as-is in the
    /// settings view; the CLI owns the wording, this view model does not reformat it.
    /// </summary>
    public string CliVersion
    {
        get => _cliVersion;
        private set => SetProperty(ref _cliVersion, value);
    }

    public bool IsCheckingCliVersion
    {
        get => _isCheckingCliVersion;
        private set
        {
            if (SetProperty(ref _isCheckingCliVersion, value))
            {
                CheckCliVersionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand CheckForCliUpdateCommand { get; }

    public AsyncCommand InstallCliUpdateCommand { get; }

    /// <summary>Human-readable result of the last update check, or the progress of a running install.</summary>
    public string CliUpdateStatus
    {
        get => _cliUpdateStatus;
        private set => SetProperty(ref _cliUpdateStatus, value);
    }

    /// <summary>
    /// True only when a newer Stable release was positively identified for this platform. An
    /// unreadable installed version leaves this false — see <see cref="CliUpdateAvailability"/>.
    /// </summary>
    public bool IsCliUpdateAvailable
    {
        get => _isCliUpdateAvailable;
        private set
        {
            if (SetProperty(ref _isCliUpdateAvailable, value))
            {
                InstallCliUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCliUpdateBusy
    {
        get => _isCliUpdateBusy;
        private set
        {
            if (SetProperty(ref _isCliUpdateBusy, value))
            {
                CheckForCliUpdateCommand.RaiseCanExecuteChanged();
                InstallCliUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// What the listing is ordered by. Clicking the active key again flips the direction, which is
    /// what a column header does everywhere else.
    /// </summary>
    public DriveSortKey SortKey
    {
        get => _sortKey;
        private set
        {
            if (SetProperty(ref _sortKey, value))
            {
                RaiseSortStates();
            }
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        private set
        {
            if (SetProperty(ref _sortDescending, value))
            {
                RaiseSortStates();
            }
        }
    }

    /// <summary>Non-empty only while a filter is hiding something.</summary>
    public string FilterSummary
    {
        get => _filterSummary;
        private set => SetProperty(ref _filterSummary, value);
    }

    public bool IsSortedByName => SortKey == DriveSortKey.Name;

    public bool IsSortedBySize => SortKey == DriveSortKey.Size;

    public bool IsSortedByModified => SortKey == DriveSortKey.Modified;

    public bool IsSortedByKind => SortKey == DriveSortKey.Kind;

    /// <summary>The arrow shown next to the active key.</summary>
    public string SortDirectionGlyph => SortDescending ? "▼" : "▲";

    /// <summary>
    /// Whether the recursive scan is in flight. Separate from <see cref="IsLoading"/> on purpose:
    /// this runs for minutes, and gating every button in the window on it (which is what IsLoading
    /// does) would make the app look hung while the user is free to keep browsing.
    /// </summary>
    public bool IsDeepScanRunning
    {
        get => _isDeepScanRunning;
        private set
        {
            if (SetProperty(ref _isDeepScanRunning, value))
            {
                ScanFolderDeeplyCommand.RaiseCanExecuteChanged();
                CancelDeepScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The listing's presentation. Changing it does not touch <see cref="RootItems"/> — the row
    /// view models are presentation-independent, so only the items control's panel and template
    /// change, and the current selection survives a mode switch.
    /// </summary>
    public DriveViewMode ViewMode
    {
        get => _viewMode;
        private set
        {
            if (!SetProperty(ref _viewMode, value))
            {
                return;
            }

            // Spelled out as booleans rather than bound through an enum converter: compiled
            // bindings need a resolvable type, and the repo keeps derived view state on the
            // view model (see SelectedKind, CommandConsoleToggleGlyph).
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(IsIconsView));
            OnPropertyChanged(nameof(IsGalleryView));
            PersistSettings();
        }
    }

    public bool IsListView => ViewMode == DriveViewMode.List;

    public bool IsIconsView => ViewMode == DriveViewMode.Icons;

    public bool IsGalleryView => ViewMode == DriveViewMode.Gallery;

    /// <summary>
    /// Which of the two top-level views is on screen. The explorer (folder browser) and the
    /// settings view (CLI connection + sync pairs) share one window instead of stacking dialogs,
    /// so this is a plain view switch, not a navigation stack.
    /// </summary>
    public bool IsSettingsView
    {
        get => _isSettingsView;
        private set => SetProperty(ref _isSettingsView, value);
    }

    /// <summary>
    /// The startup update check, for the composition root to fire and forget. Kept off the
    /// constructor so building a view model never reaches the network, and swallowing here rather
    /// than in <see cref="CheckForCliUpdateAsync"/> because a background check nobody asked for must
    /// not be able to raise a dialog or take down the process.
    /// </summary>
    public async Task CheckForCliUpdateInBackgroundAsync()
    {
        try
        {
            await CheckForCliUpdateCommand.ExecuteAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

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
                // A different executable is a different version; what was read no longer applies,
                // and neither does an update offer that was computed against the old one.
                CliVersion = UnknownCliVersion;
                _availableRelease = null;
                IsCliUpdateAvailable = false;
                CliUpdateStatus = "Not checked yet.";
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

    /// <summary>Whether the Status panel's per-item fields (as opposed to the current-folder ones) have anything to show.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetProperty(ref _hasSelection, value);
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
        // Sync's crash recovery (docs/PLAN-LOCAL-SYNC.md §7) belongs at app startup, before the
        // user can trigger a sync — but it must never keep the browser from loading, so a
        // failure here is reported and swallowed rather than propagated.
        try
        {
            await SyncPanel.RecoverFromPreviousRunAsync();
            await SyncPanel.InitializeAsync();
        }
        catch (Exception ex)
        {
            QueueCommandLine($"[warn] Sync startup recovery failed: {ex.Message}");
        }

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

    private bool CanScanFolderDeeply()
        => IsAuthenticated && !IsDeepScanRunning && _statsScanner is not null && _metricsStore is not null;

    /// <summary>
    /// The recursive scan of the folder on screen. One at a time, app-wide: every folder is a ~3.5 s
    /// CLI process (docs/PLAN-BROWSER-VIEWS.md M3), and two scans would compete for the executor's
    /// concurrency ceiling with each other, with the sync engine, and with the user's own browsing.
    /// </summary>
    private async Task ScanFolderDeeplyAsync()
    {
        if (_statsScanner is null || _metricsStore is null)
        {
            return;
        }

        var path = CurrentPath;
        _deepScanCts?.Cancel();
        _deepScanCts = new CancellationTokenSource();
        var token = _deepScanCts.Token;

        IsDeepScanRunning = true;
        Metrics.BeginDeepScan();

        try
        {
            var progress = new Progress<FolderScanProgress>(report =>
                Metrics.ReportDeepScanProgress(report.FoldersScanned, report.FoldersQueued));

            var metrics = await _statsScanner.ScanAsync(path, progress, token);

            // Only a finished scan is persisted; a cancelled one is shown and forgotten
            // (FolderMetricsStore.SaveAsync enforces that too, this just avoids the round-trip).
            if (metrics.IsComplete)
            {
                await _metricsStore.SaveAsync(metrics, CancellationToken.None);
            }

            // No Dispatcher hop anywhere in this method: the command is invoked from the UI thread,
            // so every await here resumes on it. Marshalling explicitly would also make the method
            // untestable — Dispatcher.UIThread.InvokeAsync never completes without a running
            // Avalonia dispatcher, which no unit test has.
            //
            // The user may have navigated away during the minutes this took. Their metrics panel now
            // describes a different folder, so showing this result there would simply be wrong — the
            // stored row is still theirs to find on the way back.
            if (CurrentPath == path)
            {
                Metrics.Update(metrics);
            }

            StatusMessage = metrics.IsComplete
                ? $"Analizadas {metrics.ScannedFolderCount} carpetas en {path}."
                : $"Análisis de {path} cancelado tras {metrics.ScannedFolderCount} carpetas.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = FormatCliError(path, ex);
            IsWarning = true;
        }
        catch (DbException ex)
        {
            // The scan itself succeeded; only storing it failed. Say so rather than implying the
            // minutes were wasted.
            StatusMessage = $"Se analizó {path} pero no se pudo guardar el resultado: {ex.Message}";
            IsWarning = true;
        }
        finally
        {
            IsDeepScanRunning = false;
            Metrics.EndDeepScan();
        }
    }

    private async Task CancelDeepScanAsync()
    {
        _deepScanCts?.Cancel();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Throws away the stored recursive metrics that a change at <paramref name="path"/> makes
    /// wrong — the folder itself, everything under it, and every folder above it. Best effort: a
    /// failure here must not turn a successful upload or delete into an error message.
    /// </summary>
    private async Task InvalidateDeepMetricsAsync(string path)
    {
        if (_metricsStore is null)
        {
            return;
        }

        try
        {
            await _metricsStore.InvalidateForChangeAtAsync(path);
        }
        catch (DbException)
        {
            // A stale metric is a wrong number on screen; a crashed delete is a lost file. If the
            // cache can't be invalidated the user's next scan will fix it.
        }
    }

    private bool CanGoBack() => !IsLoading && IsAuthenticated && _navigationHistory.Count > 0;

    private bool CanUpload() => !IsLoading && IsAuthenticated;

    private bool CanCreateFolder() => !IsLoading && IsAuthenticated;

    private bool CanDownloadActivity() => _commandLog.Count > 0;

    private bool CanClearActivity() => _commandLog.Count > 0;

    private async Task AuthenticateAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Opening Proton Drive authentication in your browser...";
            await _provider.Auth.AuthenticateAsync();
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
            await _provider.Auth.LogoutAsync();
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
            await LoadFolderAsync(CurrentPath, clearSelection: false, forceFreshRemoteView: true);
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
            await _provider.Operations.UploadFilesAsync(files, CurrentPath, strategy);
            StatusMessage = $"Uploaded {files.Count} file(s) to {CurrentPath}.";
            await InvalidateDeepMetricsAsync(CurrentPath);

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
            await _provider.Operations.CreateFolderAsync(CurrentPath, folderName);
            StatusMessage = $"Created folder '{folderName}' in {CurrentPath}.";
            
            // Update DB immediately
            var newFolderPath = ProtonDriveService.CombinePath(CurrentPath, folderName);
            await _cacheService.AddOrUpdateItemAsync(CurrentPath, new DriveItem(newFolderPath, folderName, true));
            await InvalidateDeepMetricsAsync(newFolderPath);

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
            await _provider.Operations.DownloadFileAsync(item.Path, folder);
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
            await _provider.Operations.RenameItemAsync(item.Path, newName);
            StatusMessage = $"Renamed {item.Name} to {newName}.";

            // Update DB immediately
            var parentPath = GetParentPath(item.Path);
            var newPath = ProtonDriveService.CombinePath(parentPath, newName);
            await _cacheService.RemoveItemAsync(item.Path);
            await _cacheService.AddOrUpdateItemAsync(parentPath, item with { Path = newPath, Name = newName });
            // Both paths: the old subtree's metrics are gone, and the new name's ancestors no
            // longer describe what's under them.
            await InvalidateDeepMetricsAsync(item.Path);
            await InvalidateDeepMetricsAsync(newPath);

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
            await _provider.Operations.CopyItemAsync(item.Path, CurrentPath, string.IsNullOrEmpty(newName) ? null : newName);
            StatusMessage = $"Copied {item.Name} successfully.";
            await InvalidateDeepMetricsAsync(CurrentPath);
            
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
            await _provider.Operations.TrashItemAsync(item.Path);
            StatusMessage = $"Moved {item.Name} to trash.";

            // Update DB immediately
            await _cacheService.RemoveItemAsync(item.Path);
            await InvalidateDeepMetricsAsync(item.Path);

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
        SelectRow(RootItems.FirstOrDefault(node => node.Item.Path == item.Path));
        SelectItem(item);

        if (!item.IsFolder)
        {
            return;
        }

        await NavigateIntoAsync(item.Path);
    }

    private void SelectRow(DriveNodeViewModel? node)
    {
        if (_selectedNode is not null)
        {
            _selectedNode.IsSelected = false;
        }

        _selectedNode = node;

        if (node is not null)
        {
            node.IsSelected = true;
        }
    }

    private async Task LoadFolderAsync(string path, bool clearSelection, bool forceFreshRemoteView = false)
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
                _ = FetchFromCliAndUpdateCacheAsync(path, clearSelection, forceFreshRemoteView, token);
                return;
            }
            else
            {
                // Clear items while waiting for CLI
                DisplayItems(Array.Empty<DriveItem>());
                await FetchFromCliAndUpdateCacheAsync(path, clearSelection, forceFreshRemoteView, token);
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

    /// <summary>
    /// Discards the CLI's cached view of the remote tree when it can no longer be trusted, so the
    /// listing that follows comes from the server. Explicit only on the user's own Refresh; on
    /// navigation it fires at most once per <see cref="RemoteViewFreshnessWindow"/>.
    /// </summary>
    private async Task EnsureFreshRemoteViewAsync(bool force, CancellationToken token)
    {
        if (_remoteViewFreshness.ShouldRefresh(_timeProvider.GetUtcNow(), force) && _provider.RemoteView is not null)
        {
            await _provider.RemoteView.ResetRemoteCacheAsync(token);
        }
    }

    private async Task FetchFromCliAndUpdateCacheAsync(string path, bool clearSelection, bool forceFreshRemoteView, CancellationToken token)
    {
        try
        {
            await EnsureFreshRemoteViewAsync(forceFreshRemoteView, token);

            // 2. Fetch from CLI
            var items = await _provider.Operations.ListFolderAsync(path, token);

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
        catch (DbException ex)
        {
            // The local cache database, not the CLI. `DbException` is not an
            // `InvalidOperationException`, so before this it escaped both catches — and on the
            // cached path this method is fire-and-forget, so nothing observed it at all: the listing
            // silently stayed at whatever the cache held. The usual cause is write contention with
            // the sync engine over the shared cache.db, which now fails in seconds instead of
            // hanging, so it needs to actually say something.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Loaded {path} but could not update the local cache: {ex.Message}";
                IsWarning = true;
            });
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

    /// <summary>
    /// Replaces the listing with <paramref name="items"/>: remembers them as the folder's full
    /// contents, then renders the filtered, sorted view of them.
    ///
    /// Internal rather than private so tests can populate the listing directly. The production
    /// route (<see cref="FetchFromCliAndUpdateCacheAsync"/>) marshals through
    /// <c>Dispatcher.UIThread.InvokeAsync</c>, which never completes without a running Avalonia
    /// dispatcher — so a test that went through the CLI path to get rows on screen would hang
    /// rather than fail.
    /// </summary>
    internal void DisplayItems(IEnumerable<DriveItem> items)
    {
        _loadedItems = items as IReadOnlyList<DriveItem> ?? items.ToList();

        // A filter belongs to the folder it was chosen in. Carrying it into the next folder would
        // hide files the user has never filtered and make the listing look wrong, or empty.
        if (_kindFilter is not null && !_loadedItems.Any(item => FileKindClassifier.Classify(item.Name, item.IsFolder) == _kindFilter))
        {
            _kindFilter = null;
        }

        RenderItems();
    }

    private void RenderItems()
    {
        // Rebuilding replaces every row's view-model, so the previous selection highlight would
        // otherwise vanish even on a plain refresh (which intentionally keeps the side panel's
        // selection) — carry it forward onto whichever new row still matches that path.
        var previouslySelectedPath = _selectedNode?.Path;
        _selectedNode = null;

        var visible = _kindFilter is null
            ? _loadedItems
            : _loadedItems.Where(item => FileKindClassifier.Classify(item.Name, item.IsFolder) == _kindFilter).ToList();

        RootItems.Clear();
        foreach (var item in DriveItemSorter.Sort(visible, SortKey, SortDescending))
        {
            var node = new DriveNodeViewModel(item, HandleRowClickAsync, DownloadItemAsync, TrashItemAsync, RenameItemAsync, CopyItemAsync, HandleUnexpectedError);
            if (previouslySelectedPath is not null && item.Path == previouslySelectedPath)
            {
                node.IsSelected = true;
                _selectedNode = node;
            }

            RootItems.Add(node);
        }

        // Computed here rather than at each call site so the cached paint and the CLI result both
        // update it, and so the numbers can never disagree with the rows actually on screen.
        // Built from everything loaded, never from the filtered rows: metrics answer "what is in
        // this folder", and a total that silently followed the filter would be a different question
        // wearing the same label.
        var metrics = FolderMetricsCalculator.FromChildren(CurrentPath, _loadedItems, _timeProvider.GetUtcNow());
        Metrics.Update(metrics);
        RebuildKindFilters(metrics);

        // Fire and forget: a stored folder size is a nice-to-have annotation, and the rows must
        // paint without waiting on the database.
        _ = AnnotateFolderSizesAsync(CurrentPath, RootItems.Where(node => node.IsFolder).ToList());
    }

    /// <summary>
    /// Fills in <see cref="DriveNodeViewModel.DeepSizeText"/> for the folders on screen that someone
    /// has already scanned. One query for the whole listing, not one per row.
    /// </summary>
    private async Task AnnotateFolderSizesAsync(string path, IReadOnlyList<DriveNodeViewModel> folders)
    {
        if (_metricsStore is null || folders.Count == 0)
        {
            return;
        }

        Dictionary<string, FolderMetrics> stored;
        try
        {
            stored = await _metricsStore.GetManyAsync(folders.Select(folder => folder.Path).ToList());
        }
        catch (DbException)
        {
            // Contention with the sync engine over cache.db. An annotation is not worth a warning.
            return;
        }

        if (stored.Count == 0)
        {
            return;
        }

        // Posted, not awaited: this method is fire-and-forget from DisplayItems, so nothing needs the
        // completion — and awaiting a dispatcher hop would hang wherever no dispatcher loop is
        // running, which includes every unit test.
        Dispatcher.UIThread.Post(() =>
        {
            // The listing may have been rebuilt while the query ran; these row view models would
            // then be orphans, and writing to them would leave the new rows blank instead.
            if (CurrentPath != path)
            {
                return;
            }

            foreach (var folder in folders)
            {
                folder.DeepSizeText = stored.TryGetValue(folder.Path, out var metrics)
                    ? ByteSize.Format(metrics.TotalSize)
                    : null;
            }
        });
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
        SelectedModified = item.ModifiedAt is { } modifiedAt ? modifiedAt.ToLocalTime().ToString("g") : "None";
        SelectedOwner = item.Owner ?? "None";
        SelectedShared = item.IsShared ? "Yes" : "No";
        HasSelection = true;
        StatusMessage = $"Selected {item.Name}.";
    }

    private void ClearSelection()
    {
        _selectedNode = null;
        SelectedName = "None";
        SelectedKind = "None";
        SelectedPath = "None";
        SelectedSize = "None";
        SelectedModified = "None";
        SelectedOwner = "None";
        SelectedShared = "None";
        HasSelection = false;
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
        _settings.Update(settings =>
        {
            settings.CliPath = CliPath;
            settings.IsAuthenticated = IsAuthenticated;
            settings.ViewMode = ViewMode.ToString();
            settings.SortKey = SortKey.ToString();
            settings.SortDescending = SortDescending;
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
        CheckCliVersionCommand.RaiseCanExecuteChanged();
        CheckForCliUpdateCommand.RaiseCanExecuteChanged();
        InstallCliUpdateCommand.RaiseCanExecuteChanged();
    }

    private async Task ToggleCommandConsoleAsync()
    {
        IsCommandConsoleVisible = !IsCommandConsoleVisible;
        await Task.CompletedTask;
    }

    private void RaiseSortStates()
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedBySize));
        OnPropertyChanged(nameof(IsSortedByModified));
        OnPropertyChanged(nameof(IsSortedByKind));
        OnPropertyChanged(nameof(SortDirectionGlyph));
    }

    private void RebuildKindFilters(FolderMetrics metrics)
    {
        KindFilters.Clear();
        if (metrics.Buckets.Count <= 1)
        {
            // One kind (or none) means every chip would be a no-op, and "Todos" alone is just noise.
            FilterSummary = string.Empty;
            return;
        }

        KindFilters.Add(new KindFilterViewModel(null, metrics.FileCount + metrics.FolderCount, ApplyKindFilterAsync, HandleUnexpectedError)
        {
            IsActive = _kindFilter is null,
        });

        foreach (var bucket in metrics.Buckets.OrderByDescending(bucket => bucket.Count).ThenBy(bucket => bucket.Kind.ToString(), StringComparer.Ordinal))
        {
            KindFilters.Add(new KindFilterViewModel(bucket.Kind, bucket.Count, ApplyKindFilterAsync, HandleUnexpectedError)
            {
                IsActive = _kindFilter == bucket.Kind,
            });
        }

        FilterSummary = _kindFilter is null
            ? string.Empty
            : $"Mostrando {RootItems.Count:n0} de {metrics.FileCount + metrics.FolderCount:n0} elementos.";
    }

    private async Task ApplyKindFilterAsync(FileKind? kind)
    {
        // Clicking the active chip clears it, so the filter can always be undone from where it was
        // applied, not only from "Todos".
        _kindFilter = _kindFilter == kind ? null : kind;
        RenderItems();
        await Task.CompletedTask;
    }

    private async Task SortByAsync(DriveSortKey key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            // A new key starts ascending, except the two where "most" is the interesting end: the
            // reason to sort by size or date is almost always to find the biggest or the newest.
            SortDescending = key is DriveSortKey.Size or DriveSortKey.Modified;
        }

        PersistSettings();

        // Re-sorted from what was loaded, not from the visible rows: sorting must not also drop
        // whatever the active filter is hiding.
        RenderItems();
        await Task.CompletedTask;
    }

    private async Task SetViewModeAsync(DriveViewMode mode)
    {
        ViewMode = mode;
        await Task.CompletedTask;
    }

    private async Task ShowExplorerAsync()
    {
        IsSettingsView = false;
        await Task.CompletedTask;
    }

    private async Task ShowSettingsAsync()
    {
        IsSettingsView = true;

        // Read it on the way in, so the settings view is never showing a stale or empty version,
        // but only once per configured path — the CLI costs a whole process launch (~3.5s cold).
        if (CliVersion == UnknownCliVersion && !string.IsNullOrWhiteSpace(CliPath))
        {
            await CheckCliVersionAsync();
        }
    }

    private bool CanCheckCliVersion() => !IsCheckingCliVersion && !string.IsNullOrWhiteSpace(CliPath);

    private async Task CheckCliVersionAsync()
    {
        IsCheckingCliVersion = true;
        try
        {
            // Diagnostics is only ever null for a provider with no external binary to version
            // (docs/PLAN-CLOUD-PROVIDERS.md §2.6); the settings UI stops offering this command for
            // such a provider as of P5. Today's only provider (Proton) always has one.
            var version = _provider.Diagnostics is not null
                ? await _provider.Diagnostics.GetVersionAsync()
                : null;
            CliVersion = string.IsNullOrWhiteSpace(version)
                ? "The CLI reported no version."
                : version;
        }
        catch (InvalidOperationException ex)
        {
            // Includes CliException. The CLI's own text is the most useful thing on screen here:
            // if `--version` is not the flag this build understands, the user sees exactly that.
            CliVersion = $"Unavailable: {ex.Message}";
        }
        catch (FileNotFoundException ex)
        {
            CliVersion = $"Unavailable: {ex.Message}";
        }
        finally
        {
            IsCheckingCliVersion = false;
        }
    }

    private bool CanCheckForCliUpdate() => _releaseFeed is not null && !IsCliUpdateBusy;

    /// <summary>
    /// Compares the installed CLI against Proton's published Stable release. This is the app's only
    /// outbound network call; everything else goes through the CLI process.
    /// </summary>
    private async Task CheckForCliUpdateAsync()
    {
        if (_releaseFeed is null)
        {
            CliUpdateStatus = "Update checking is not available.";
            return;
        }

        IsCliUpdateBusy = true;
        try
        {
            // The comparison needs a version to compare against, and the user may never have
            // opened the settings view this session.
            if (CliVersion == UnknownCliVersion && !string.IsNullOrWhiteSpace(CliPath))
            {
                await CheckCliVersionAsync();
            }

            var release = await _releaseFeed.GetLatestStableAsync();
            if (release is null)
            {
                _availableRelease = null;
                IsCliUpdateAvailable = false;
                CliUpdateStatus = "Proton publishes no Stable build for this platform.";
                return;
            }

            switch (CliVersionComparer.Compare(CliVersion, release.Version))
            {
                case CliUpdateAvailability.UpdateAvailable:
                    _availableRelease = release;
                    IsCliUpdateAvailable = true;
                    CliUpdateStatus = $"Version {release.Version} is available ({release.ReleaseDate}).";
                    break;

                case CliUpdateAvailability.UpToDate:
                    _availableRelease = null;
                    IsCliUpdateAvailable = false;
                    CliUpdateStatus = $"Up to date — {release.Version} is the current Stable release.";
                    break;

                default:
                    // Refusing to offer an install here is the point: overwriting a working CLI on
                    // the strength of a version string we couldn't read is the one outcome worse
                    // than not updating.
                    _availableRelease = null;
                    IsCliUpdateAvailable = false;
                    CliUpdateStatus = $"Stable is {release.Version}, but the installed version could not be read — not offering an update.";
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _availableRelease = null;
            IsCliUpdateAvailable = false;
            CliUpdateStatus = $"Could not reach Proton's release manifest: {ex.Message}";
        }
        finally
        {
            IsCliUpdateBusy = false;
        }
    }

    private bool CanInstallCliUpdate()
        => IsCliUpdateAvailable && _availableRelease is not null && !IsCliUpdateBusy && !string.IsNullOrWhiteSpace(CliPath);

    private async Task InstallCliUpdateAsync()
    {
        var release = _availableRelease;
        if (release is null)
        {
            return;
        }

        // A scan or transfer in flight is holding the CLI. The rename itself is atomic and an
        // already-running process keeps its own inode, so this is not about corrupting the swap —
        // it is that the next call in that same cycle would land on a different binary version
        // mid-operation, which is not a state worth reasoning about.
        if (_isSyncInProgress())
        {
            CliUpdateStatus = "A sync is running. Wait for it to finish, then update.";
            return;
        }

        IsCliUpdateBusy = true;
        try
        {
            CliUpdateStatus = $"Downloading {release.Version}…";
            await _updateInstaller.InstallAsync(
                release,
                CliPath,
                onProgress: bytes => Dispatcher.UIThread.Post(
                    () => CliUpdateStatus = $"Downloading {release.Version}… {bytes / (1024 * 1024)} MB"));

            _availableRelease = null;
            IsCliUpdateAvailable = false;
            CliVersion = UnknownCliVersion;
            await CheckCliVersionAsync();
            CliUpdateStatus = $"Updated to {release.Version}. Verified against the published SHA-512.";
        }
        catch (CliUpdateException ex)
        {
            // Includes the checksum mismatch, which leaves the old binary in place by design.
            CliUpdateStatus = ex.Message;
            IsWarning = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            CliUpdateStatus = $"Update failed, the existing CLI was kept: {ex.Message}";
            IsWarning = true;
        }
        finally
        {
            IsCliUpdateBusy = false;
        }
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
        lock (_commandLogGate)
        {
            _pendingCommandLines.Clear();
        }

        _commandLog.Clear();
        CommandLogText = "No CLI command running.";
        ActiveCommand = "Idle";
        RaiseCommandStates();
        await Task.CompletedTask;
    }

    private void OnCommandStarted(object? sender, CliCommandStartedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => ActiveCommand = e.CommandText);
        QueueCommandLine($"> {e.CommandText}");
    }

    private void OnCommandOutput(object? sender, CliCommandOutputEventArgs e)
        => QueueCommandLine(e.IsError ? $"[err] {e.Text}" : e.Text);

    private void OnCommandFinished(object? sender, CliCommandFinishedEventArgs e)
    {
        QueueCommandLine(e.Succeeded ? $"[done] exit {e.ExitCode}" : $"[fail] exit {e.ExitCode}");
        Dispatcher.UIThread.Post(() => ActiveCommand = "Idle");
    }

    private void OnListingParseWarning(object? sender, string message)
        => QueueCommandLine($"[warn] {message}");

    /// <summary>
    /// Buffers a console line and makes sure exactly one flush is pending.
    ///
    /// The old version posted to the UI thread per line and rebuilt the whole console text there,
    /// which re-shaped ~300 KB of text through HarfBuzz on every single line (see
    /// <see cref="CommandLogBuffer"/> for the captured stack). Now the lines accumulate and one
    /// flush drains them, at <see cref="DispatcherPriority.Background"/> so it runs *after* input and
    /// layout — a burst of CLI output can no longer outrun the user's clicks.
    /// </summary>
    private void QueueCommandLine(string line)
    {
        lock (_commandLogGate)
        {
            _pendingCommandLines.Add(line);
            if (_commandLogFlushScheduled)
            {
                return;
            }

            _commandLogFlushScheduled = true;
        }

        Dispatcher.UIThread.Post(FlushCommandLog, DispatcherPriority.Background);
    }

    private void FlushCommandLog()
    {
        List<string> batch;
        lock (_commandLogGate)
        {
            _commandLogFlushScheduled = false;
            if (_pendingCommandLines.Count == 0)
            {
                return;
            }

            batch = [.. _pendingCommandLines];
            _pendingCommandLines.Clear();
        }

        var countBefore = _commandLog.Count;
        _commandLog.AddRange(batch);
        CommandLogText = _commandLog.Render();

        // Only the two activity commands depend on the line count, and only on the empty/non-empty
        // transition. Re-raising all thirteen on every line was pure waste on the UI thread.
        if (countBefore == 0 && _commandLog.Count > 0)
        {
            DownloadActivityCommand.RaiseCanExecuteChanged();
            ClearActivityCommand.RaiseCanExecuteChanged();
        }
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
            QueueCommandLine($"[err] Unexpected error: {ex}");
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
