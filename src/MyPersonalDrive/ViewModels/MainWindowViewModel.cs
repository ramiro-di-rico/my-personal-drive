using System.Collections.ObjectModel;
using System.Data.Common;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Providers.OneDrive;
using MyPersonalDrive.Services.Providers.GoogleDrive;
using MyPersonalDrive.Services.Providers.Generic;
using MyPersonalDrive.ViewModels.Local;
using Avalonia.Threading;

namespace MyPersonalDrive.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    // Not readonly (P7 Phase B, docs/PLAN-CLOUD-PROVIDERS.md): SwitchBrowserAccountAsync reassigns
    // these five plus _rootPath below when the browsed account changes, instead of the previous
    // "persist a preference and ask for a restart" behavior. See _browserSessions.
    private ICloudDriveProvider _provider;
    private DriveCacheService _cacheService;
    private readonly LocalFileSystemService _localFileSystem;
    private readonly AppSettingsService _settings;
    private readonly IProviderCatalog _providerCatalog;
    private FolderMetricsStore? _metricsStore;
    private FolderStatsScanner? _statsScanner;
    private CancellationTokenSource? _deepScanCts;
    private ITextFilePreviewLoader? _previewLoader;
    private IImageFilePreviewLoader? _imagePreviewLoader;
    private IPdfFilePreviewLoader? _pdfPreviewLoader;
    private CancellationTokenSource? _previewCts;
    private readonly Stack<string> _navigationHistory = new();
    private readonly TimeProvider _timeProvider;
    private readonly RemoteViewFreshnessPolicy _remoteViewFreshness = new();
    private CancellationTokenSource? _cts;
    private DriveNodeViewModel? _selectedNode;
    private string? _selectionAnchorPath;
    private string _rootPath;

    /// <summary>
    /// True for the duration of <see cref="SwitchBrowserAccountAsync"/>. Guards
    /// <see cref="SelectedProviderIndex"/>'s setter against the header ComboBox writing its own
    /// transient selection back mid-switch (docs/PLAN-UX-ROUND-2.md §11.3).
    /// </summary>
    private bool _isSwitchingProvider;

    /// <summary>
    /// One browsable account's whole toolchain — everything <see cref="SwitchBrowserAccountAsync"/>
    /// needs to swap live. The primary account (registered by the constructor) is always
    /// <c>_browserSessions[0]</c>; <see cref="AddBrowsableAccount"/> only ever appends, mirroring
    /// <c>SyncPanelViewModel.AddAccount</c>'s own additive shape (P7 Phase A) so existing call sites
    /// and tests that never call it are unaffected.
    /// </summary>
    private sealed record BrowserAccountSession(
        ICloudDriveProvider Provider,
        DriveCacheService CacheService,
        FolderMetricsStore? MetricsStore,
        FolderStatsScanner? StatsScanner,
        ITextFilePreviewLoader? PreviewLoader,
        IImageFilePreviewLoader? ImagePreviewLoader,
        IPdfFilePreviewLoader? PdfPreviewLoader);

    private readonly List<BrowserAccountSession> _browserSessions = new();
    // Doubled from CommandLogBuffer's own default (200): with two provider sessions able to be
    // active at once (P7), one interleaved buffer now serves two sources, and a burst from one
    // must not push the other's entire recent history out.
    private readonly CommandLogBuffer _commandLog = new(maxLines: CommandLogBuffer.MaxLines * 2);

    /// <summary>
    /// Guards <see cref="_pendingCommandLines"/>, which the CLI executor's events fill from whatever
    /// thread the process I/O happened on — and now from up to eight of them at once, since read-only
    /// commands run concurrently.
    /// </summary>
    private readonly object _commandLogGate = new();
    private readonly List<string> _pendingCommandLines = new();
    private bool _commandLogFlushScheduled;
    private string _cliPath;
    private string _oneDriveClientId;
    private string _googleDriveClientId;
    private string _googleDriveClientSecret;
    private string _currentPath;
    private readonly StatusSurface _status;
    private bool _hasRenderedListing;
    private bool _isLoading;
    private bool _isAuthenticated;
    private bool _isCommandConsoleVisible = true;
    private int _activeOperationCount;
    private string? _lastLogLine;
    private bool _showOnlyWarningsAndErrors;
    private string _logSearchText = string.Empty;
    private double _commandConsoleMaxHeight = 180;
    private double _commandConsoleHeight = AppSettings.DefaultCommandConsoleHeight;
    private double _commandConsoleOpacity = 1;
    private bool _commandConsoleHitTestVisible = true;
    private string _activeCommand = Localizer.Instance.T(StringKeys.Console.Idle);
    private string _commandLogText = Localizer.Instance.T(StringKeys.Console.NoCommandRunning);
    private string _commandConsoleToggleLabel = Localizer.Instance.T(StringKeys.Console.ToggleHide);
    private string _commandConsoleToggleGlyph = "▼";
    private string _selectedName = Localizer.Instance.T(StringKeys.Common.None);
    private string _selectedKind = Localizer.Instance.T(StringKeys.Common.None);
    private string _selectedPath = Localizer.Instance.T(StringKeys.Common.None);
    private string _selectedSize = Localizer.Instance.T(StringKeys.Common.None);
    private string _selectedModified = Localizer.Instance.T(StringKeys.Common.None);
    private string _selectedOwner = Localizer.Instance.T(StringKeys.Common.None);
    private string _selectedShared = Localizer.Instance.T(StringKeys.Common.None);
    private bool _hasSelection;
    private DriveItem? _selectedItem;
    private MainView _activeView = MainView.Explorer;
    private bool _isViewerVisible;
    private bool _isViewerLoading;
    private string _viewerTitle = Localizer.Instance.T(StringKeys.Viewer.Title);
    private string _viewerPath = string.Empty;
    private string _viewerText = string.Empty;
    private string _viewerNote = string.Empty;
    private byte[]? _viewerImageBytes;
    private IReadOnlyList<byte[]>? _viewerPdfPages;
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
    private string _searchText = string.Empty;
    private string _filterSummary = string.Empty;
    private bool _sortDescending;
    private readonly Func<bool> _isSyncInProgress;
    private string _theme = "Default";
    private int _bandwidthLimitKbps;
    private double _viewerZoom;
    private string _defaultSyncFolder = string.Empty;
    private string _connectionStatus = Localizer.Instance.T(StringKeys.Connection.StateOnline);
    private string _connectionStatusKind = "Online";
    private string _connectionStatusDescription = Localizer.Instance.T(StringKeys.Connection.Initial);
    private long _quotaUsedBytes;

    // Tri-state, because a long defaulting to 0 cannot tell "empty account" from "the provider
    // never told us" — and the app was rendering both as "0 B / 500 GB (0% used)" above a folder
    // full of files (docs/PLAN-UX-ROUND-2.md §3).
    private bool _quotaUsedIsKnown;
    private bool _quotaUsedIsPartial;
    private DriveErrorKind _lastErrorKind = DriveErrorKind.Unknown;

    private bool _isStatusPanelVisible;
    private bool _isLocalExplorerPanelVisible;

    /// <summary>
    /// What the settings view's provider picker and header dropdown list — see docs/PLAN-CLOUD-PROVIDERS.md P5/P6.
    /// Dynamically reflects the live account identities and connection statuses.
    ///
    /// One stable instance for the lifetime of the ViewModel, populated once in the constructor and
    /// updated in place afterward by <see cref="RefreshAvailableProviders"/> — never reassigned to a
    /// brand-new collection. The header ComboBox's <c>ItemsSource</c> binds to this exact instance;
    /// swapping in a new collection object on every provider switch (the original design, and every
    /// attempted fix short of this one) is what made switching directly between two providers
    /// unreliable: Avalonia's <c>SelectingItemsControl</c> resets or mis-tracks
    /// <c>SelectedItem</c>/<c>SelectedIndex</c> whenever <c>ItemsSource</c> itself changes identity,
    /// no matter how carefully the selected value is kept in sync afterward
    /// (docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2 — three prior attempts, each live-tested and
    /// each still broken, all treated a symptom of this instead of the cause).
    /// </summary>
    public ObservableCollection<ProviderDescriptor> AvailableProviders { get; } = new();

    /// <summary>
    /// Recomputes every entry's live fields (<c>AccountIdentity</c>/<c>IsAuthenticated</c>) and
    /// writes them into <see cref="AvailableProviders"/> by index — each assignment is an
    /// <see cref="ObservableCollection{T}"/> element replacement (a granular
    /// <c>CollectionChanged</c> notification), not a reassignment of the collection itself. Called
    /// once from the constructor to populate it, and again after anything that can change a
    /// provider's live auth state (sign-in/out, a live account switch).
    /// </summary>
    private void RefreshAvailableProviders()
    {
        var settings = _settings.Load();
        var available = (_providerCatalog ?? new ProviderCatalog()).Available;

        for (var i = 0; i < available.Count; i++)
        {
            var desc = available[i];

            // The active provider's live in-memory flag is fresher than settings (which may not
            // have been persisted yet mid-session); every other provider is read from settings.
            var isAuth = desc.Id == _provider.Id ? _isAuthenticated : settings.IsProviderAuthenticated(desc.Id);

            // OneDrive's account label lives on its live GraphAuthenticator, not settings, until
            // AuthenticateAsync persists it — settings can lag behind what's actually signed in.
            // The "not signed in" placeholder is this view model's own (OneDriveAccountLabel
            // below); GraphAuthenticator reports null when there is no session, which the pattern
            // already excludes. The comparison against that sentence was redundant, and would
            // have quietly stopped matching the moment the sentence was translated.
            var liveLabel = _provider is OneDriveProvider { Auth: GraphAuthenticator { AccountLabel: { } label } }
                ? label
                : null;

            var persistedLabel = settings.ProviderAccountLabel(desc.Id);
            var identity = liveLabel
                ?? (!string.IsNullOrWhiteSpace(persistedLabel) ? persistedLabel : null)
                ?? (isAuth ? PlaceholderIdentity(desc.Id) : null);

            var updated = desc with { AccountIdentity = identity, IsAuthenticated = isAuth };

            if (i < AvailableProviders.Count)
            {
                // Only when something the user can see actually differs. Replacing an element is
                // what perturbs the ComboBox's selection (see the raise at the end of this
                // method), and a provider *switch* changes no descriptor's fields at all — so
                // before this check every switch pointlessly replaced all five entries and made
                // the control fight the view model for the selection (docs/PLAN-UX-ROUND-2.md
                // §11.3). ProviderDescriptor.Equals is Id-only by design, so it cannot answer
                // this; the displayed fields have to be compared by hand.
                var current = AvailableProviders[i];
                if (current.Id != updated.Id
                    || current.IsAuthenticated != updated.IsAuthenticated
                    || current.AccountIdentity != updated.AccountIdentity)
                {
                    AvailableProviders[i] = updated;
                }
            }
            else
            {
                AvailableProviders.Add(updated);
            }
        }

        // Avalonia's SelectingItemsControl clears its selection when the *selected element* is
        // replaced, even in place — and every refresh replaces element 0, which is normally the
        // selected provider. The two-way binding then writes -1 back, SelectedProviderIndex's
        // setter correctly ignores it, and nothing ever pushes the real index out again: the
        // header ComboBox renders blank while the view model still knows exactly which provider is
        // active. Raised here rather than at the call sites because two of the four already did it
        // and the sign-in path did not, which is precisely how the bug got in
        // (docs/PLAN-UX-ROUND-2.md §11; same family as PLAN-CLOUD-PROVIDERS.md P10 Appendix A2 #4).
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderIndex));
        RaiseProviderAuthStates();
    }

    /// <summary>Notifies the Conexión tabs' auth dots (docs/PLAN-UX-ROUND-2.md §8).</summary>
    private void RaiseProviderAuthStates()
    {
        OnPropertyChanged(nameof(IsProtonAuthenticated));
        OnPropertyChanged(nameof(IsOneDriveAuthenticated));
        OnPropertyChanged(nameof(IsGoogleDriveAuthenticated));
        OnPropertyChanged(nameof(IsNextcloudAuthenticated));
        OnPropertyChanged(nameof(IsS3Authenticated));
    }

    public ProviderDescriptor? SelectedProvider
    {
        get => AvailableProviders.FirstOrDefault(p => p.Id == _provider.Id) ?? AvailableProviders.FirstOrDefault();
        set
        {
            if (value is not null && value.Id != _provider.Id)
            {
                _ = SwitchProviderAndReportErrorsAsync(value.Id);
            }
        }
    }

    /// <summary>
    /// The header ComboBox binds its selection here instead of to <see cref="SelectedProvider"/>
    /// directly. A <c>SelectedItem</c> binding needs Avalonia to match the bound value against
    /// <see cref="AvailableProviders"/> by <c>Equals</c> — and since that list is rebuilt with
    /// brand-new <see cref="ProviderDescriptor"/> instances on every access, any mismatch between
    /// two recomputations (even after making <see cref="ProviderDescriptor"/>'s equality Id-only)
    /// left switching between two non-adjacent providers unreliable in practice
    /// (docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2). An index is a plain <see cref="int"/> with no
    /// such ambiguity — it can't fail to match itself — so it sidesteps the whole class of bug
    /// rather than depending on getting the equality semantics exactly right.
    /// </summary>
    public int SelectedProviderIndex
    {
        get
        {
            var providers = AvailableProviders;
            for (var i = 0; i < providers.Count; i++)
            {
                if (providers[i].Id == _provider.Id)
                {
                    return i;
                }
            }

            return providers.Count > 0 ? 0 : -1;
        }
        set
        {
            var providers = AvailableProviders;
            if (value < 0 || value >= providers.Count)
            {
                return;
            }

            // A write arriving mid-switch is the control echoing its own transient state, not the
            // user choosing anything — and acting on it starts a *second* switch from inside the
            // first. That re-entrancy is what left the picker blank after switching provider from
            // the settings view (docs/PLAN-UX-ROUND-2.md §11.3).
            if (_isSwitchingProvider)
            {
                return;
            }

            var target = providers[value];
            if (target.Id != _provider.Id)
            {
                _ = SwitchProviderAndReportErrorsAsync(target.Id);
            }
        }
    }

    public string ActiveProviderDisplayName => _provider.DisplayName;

    /// <summary>
    /// The explorer header's title/subtitle — provider-neutral (P7 Phase A surfaced this as a real
    /// gap: with OneDrive as the browsed account, a hardcoded "Proton Drive browser" header was
    /// actively misleading, not just cosmetically stale).
    /// </summary>
    public string BrowserHeaderTitle => Loc.F(StringKeys.Explorer.HeaderTitle, _provider.DisplayName);

    public string BrowserHeaderSubtitle => Loc.F(StringKeys.Explorer.HeaderSubtitle, RootPath, _provider.DisplayName);

    /// <summary>Which connection-card block the settings view shows — Proton's, OneDrive's, Google Drive's, Nextcloud's, or S3's.</summary>
    public bool IsProtonActive => _provider.Id == ProviderId.Proton;

    public bool IsOneDriveActive => _provider.Id == ProviderId.OneDrive;

    public bool IsGoogleDriveActive => _provider.Id == ProviderId.GoogleDrive;

    public bool IsNextcloudActive => _provider.Id == ProviderId.Nextcloud;

    public bool IsS3Active => _provider.Id == ProviderId.S3;

    /// <summary>
    /// Whether each provider has a stored session, for the dot on its Conexión tab. The tabs
    /// previously showed only which one was *selected* (<see cref="IsProtonActive"/> and friends),
    /// so a provider that had never been configured looked identical to a signed-in one
    /// (docs/PLAN-UX-ROUND-2.md §8). The state itself is not new — it is the same
    /// <c>AppSettings.IsProviderAuthenticated</c> the header dropdown's dot already reads through
    /// <see cref="ProviderDescriptor.IsAuthenticated"/>; it just never reached these buttons.
    /// Spelled out per provider to match the <c>Is*Active</c> family directly above.
    /// </summary>
    public bool IsProtonAuthenticated => IsProviderAuthenticated(ProviderId.Proton);

    public bool IsOneDriveAuthenticated => IsProviderAuthenticated(ProviderId.OneDrive);

    public bool IsGoogleDriveAuthenticated => IsProviderAuthenticated(ProviderId.GoogleDrive);

    public bool IsNextcloudAuthenticated => IsProviderAuthenticated(ProviderId.Nextcloud);

    public bool IsS3Authenticated => IsProviderAuthenticated(ProviderId.S3);

    // The live IsAuthenticated for the active provider, the persisted flag for the others: the
    // active one can have been signed out this session without that having been written back yet.
    private bool IsProviderAuthenticated(ProviderId id)
        => _provider.Id == id ? IsAuthenticated : _settings.Load().IsProviderAuthenticated(id);

    /// <summary>Whether the active provider has a version/self-update story to show — false for a provider with no external binary (docs/PLAN-CLOUD-PROVIDERS.md §5 item 2).</summary>
    public bool HasDiagnostics => _provider.Diagnostics is not null;

    /// <summary>The signed-in OneDrive account's label (email/name), or a "not signed in" placeholder for the card.</summary>
    public string OneDriveAccountLabel
        => _provider is OneDriveProvider oneDrive && oneDrive.Auth is GraphAuthenticator { AccountLabel: { } label }
            ? label
            : Loc.T(StringKeys.Provider.NoSession);

    /// <summary>The signed-in Google Drive account's label (email/name), or a "not signed in" placeholder for the card.</summary>
    public string GoogleDriveAccountLabel
        => _provider is GoogleDriveProvider googleDrive && googleDrive.Auth is GoogleDriveAuthenticator { AccountLabel: { } googleLabel }
            ? googleLabel
            : Loc.T(StringKeys.Provider.NoSession);

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
        FolderStatsScanner? statsScanner = null,
        IProviderCatalog? providerCatalog = null,
        ITextFilePreviewLoader? previewLoader = null,
        IImageFilePreviewLoader? imagePreviewLoader = null,
        IPdfFilePreviewLoader? pdfPreviewLoader = null,
        LocalFileSystemService? localFileSystem = null)
    {
        // Optional so the many existing view-model tests don't all have to build a database and a
        // scanner to exercise unrelated behavior. When either is absent the deep-scan command
        // simply can't execute (see CanScanFolderDeeply), which is also the honest state of the
        // feature on a machine with no CLI configured.
        _metricsStore = metricsStore;
        _statsScanner = statsScanner;
        // Optional for the same reason: a test that never opens the viewer shouldn't have to supply
        // a loader, and when it's absent the viewer simply can't open (see CanOpenViewer).
        _previewLoader = previewLoader;
        _imagePreviewLoader = imagePreviewLoader;
        _pdfPreviewLoader = pdfPreviewLoader;
        _timeProvider = timeProvider ?? TimeProvider.System;
        CliUpdate = new CliUpdateViewModel(
            () => _provider,
            releaseFeed,
            updateInstaller ?? new CliUpdateInstaller(),
            () => CliPath,
            () => _isSyncInProgress?.Invoke() ?? false,
            _status,
            HandleUnexpectedError);
        // Injected rather than read off SyncPanel directly so the refusal-while-syncing path is
        // reachable in a test without driving a real sync cycle to a chosen moment.
        // Capturing the parameter, not the SyncPanel property, which is only assigned below.
        _isSyncInProgress = isSyncInProgress ?? (() => syncPanel.IsSyncInProgress);
        _providerCatalog = providerCatalog ?? new ProviderCatalog();
        _provider = provider;
        // "/my-files" is Proton's own root folder name, not a generic convention — OneDrive (and
        // any future provider) roots at "/". Browsing "/my-files" against Graph 404s immediately
        // (verified: docs/PLAN-CLOUD-PROVIDERS.md Appendix A), which is exactly the "path no
        // longer exists" warning this was hardcoded into before a second provider existed to catch
        // it.
        _status = new StatusSurface(LocalizedText.Of(StringKeys.Status.PickCliInitial), RaiseStatusSurfaceChanged);
        _rootPath = _provider.Id == ProviderId.Proton ? "/my-files" : "/";
        _currentPath = _rootPath;
        _cacheService = cacheService;
        _settings = settings;
        SyncPanel = syncPanel;
        _localFileSystem = localFileSystem ?? new LocalFileSystemService();
        LocalExplorer = new LocalExplorerViewModel(_localFileSystem, settings, HandleUnexpectedError)
        {
            RequestSyncSelectedPathAsync = localPath => SyncPanel.AddPairAsync(new SyncPairPrefill(null, localPath)),
            FindSyncPairByPath = SyncPanel.FindPairByLocalPath,
            FindRemotePathFor = RemotePathFor,
        };

        var appSettings = settings.Load();
        _cliPath = appSettings.CliPath;
        _oneDriveClientId = appSettings.OneDriveClientId;
        _googleDriveClientId = appSettings.GoogleDriveClientId;
        _googleDriveClientSecret = appSettings.GoogleDriveClientSecret;
        // Which AppSettings field backs this VM's single IsAuthenticated flag depends on which
        // provider is active — the two providers have entirely different connection cards (CLI
        // path + version vs. sign-in/out), so there is one bool per provider in AppSettings but
        // only ever one "the active provider is signed in" flag in the VM at a time. Switching
        // providers requires a restart (§2.7), so which field to use never changes mid-session.
        _isAuthenticated = appSettings.IsProviderAuthenticated(_provider.Id);
        // Set through the field, not the property: the property persists, and the constructor has
        // no business writing settings.json back on every launch.
        _viewMode = appSettings.ViewModeOrDefault();
        _sortKey = appSettings.SortKeyOrDefault();
        _sortDescending = appSettings.SortDescending;
        _theme = appSettings.ThemeOrDefault();
        _bandwidthLimitKbps = appSettings.BandwidthLimitKbps;
        _viewerZoom = appSettings.ViewerZoomOrDefault();
        _defaultSyncFolder = appSettings.DefaultSyncFolder;
        _isStatusPanelVisible = appSettings.ShowStatusPanel;
        _isLocalExplorerPanelVisible = appSettings.ShowLocalExplorerPanel;
        _isCommandConsoleVisible = appSettings.ShowCommandConsole;
        _commandConsoleHeight = appSettings.CommandConsoleHeightOrDefault();
        _commandConsoleMaxHeight = _commandConsoleHeight + ConsoleChromeHeight;
        if (!_isCommandConsoleVisible)
        {
            // Mirrors IsCommandConsoleVisible's setter directly rather than going through it: this
            // runs before AsyncCommand fields exist, and that setter's RaiseCommandStates() would
            // null-ref against them. No PropertyChanged subscriber exists yet either, so there's
            // nothing SetProperty would have notified at this point regardless.
            _commandConsoleMaxHeight = 0;
            _commandConsoleOpacity = 0;
            _commandConsoleHitTestVisible = false;
            _commandConsoleToggleLabel = Loc.T(StringKeys.Console.ToggleShow);
            _commandConsoleToggleGlyph = "▲";
        }
        // The browsed provider's own activity is tagged like any other session's, so interleaved
        // lines from ObserveAdditionalProviderActivity (P7, both providers active at once) read
        // consistently regardless of which one happens to be on screen.
        _provider.Activity += (_, activity) => OnActivity(_provider.DisplayName, activity);
        _provider.ListingParseWarning += (_, message) => OnListingParseWarning(_provider.DisplayName, message);

        RootItems = new ObservableCollection<DriveNodeViewModel>();
        // Selecting a "largest item" row must behave exactly like clicking that row in the listing,
        // so it goes through the same handler rather than a second selection path.
        Metrics = new FolderMetricsViewModel(HandleRowClickAsync, HandleUnexpectedError);
        KindFilters = new ObservableCollection<KindFilterViewModel>();
        BreadcrumbItems = new ObservableCollection<BreadcrumbSegmentViewModel>();
        UpdateBreadcrumbs(_rootPath);
        RefreshAvailableProviders();

        AuthenticateCommand = new AsyncCommand(AuthenticateAsync, CanAuthenticate, HandleUnexpectedError);
        LogoutCommand = new AsyncCommand(LogoutAsync, CanLogout, HandleUnexpectedError);
        RefreshCommand = new AsyncCommand(RefreshAsync, CanRefresh, HandleUnexpectedError);
        BackCommand = new AsyncCommand(GoBackAsync, CanGoBack, HandleUnexpectedError);
        UploadCommand = new AsyncCommand(UploadAsync, CanUpload, HandleUnexpectedError);
        CreateFolderCommand = new AsyncCommand(CreateFolderAsync, CanCreateFolder, HandleUnexpectedError);
        ToggleCommandConsoleCommand = new AsyncCommand(ToggleCommandConsoleAsync, onError: HandleUnexpectedError);
        ToggleLocalExplorerPanelCommand = new AsyncCommand(ToggleLocalExplorerPanelAsync, onError: HandleUnexpectedError);
        ToggleLogFilterCommand = new AsyncCommand(ToggleLogFilterAsync, onError: HandleUnexpectedError);
        DownloadActivityCommand = new AsyncCommand(DownloadActivityAsync, CanDownloadActivity, HandleUnexpectedError);
        ClearActivityCommand = new AsyncCommand(ClearActivityAsync, CanClearActivity, HandleUnexpectedError);
        ShowExplorerCommand = new AsyncCommand(ShowExplorerAsync, onError: HandleUnexpectedError);
        ClearSearchCommand = new AsyncCommand(ClearSearchAsync, () => HasSearchText, HandleUnexpectedError);
        ClearFiltersCommand = new AsyncCommand(ClearFiltersAsync, () => HasActiveFilters, HandleUnexpectedError);
        DismissStatusBannerCommand = new AsyncCommand(DismissStatusBannerAsync, () => IsStatusBannerVisible, HandleUnexpectedError);
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
        ShowSyncCommand = new AsyncCommand(ShowSyncAsync, onError: HandleUnexpectedError);
        SwitchToProtonCommand = new AsyncCommand(() => SwitchBrowserAccountAsync(ProviderId.Proton), () => !IsLoading && !IsProtonActive, HandleUnexpectedError);
        SwitchToOneDriveCommand = new AsyncCommand(() => SwitchBrowserAccountAsync(ProviderId.OneDrive), () => !IsLoading && !IsOneDriveActive, HandleUnexpectedError);
        SwitchToGoogleDriveCommand = new AsyncCommand(() => SwitchBrowserAccountAsync(ProviderId.GoogleDrive), () => !IsLoading && !IsGoogleDriveActive, HandleUnexpectedError);
        SwitchToNextcloudCommand = new AsyncCommand(() => SwitchBrowserAccountAsync(ProviderId.Nextcloud), () => !IsLoading && !IsNextcloudActive, HandleUnexpectedError);
        SwitchToS3Command = new AsyncCommand(() => SwitchBrowserAccountAsync(ProviderId.S3), () => !IsLoading && !IsS3Active, HandleUnexpectedError);
        ViewSelectedFileCommand = new AsyncCommand(ViewSelectedFileAsync, CanViewSelectedFile, HandleUnexpectedError);
        CloseViewerCommand = new AsyncCommand(CloseViewerAsync, onError: HandleUnexpectedError);
        SelectAllRowsCommand = new AsyncCommand(SelectAllRowsAsync, () => RootItems.Count > 0, HandleUnexpectedError);
        DownloadSelectedCommand = new AsyncCommand(DownloadSelectedAsync, () => SelectedCount > 0, HandleUnexpectedError);
        TrashSelectedCommand = new AsyncCommand(TrashSelectedAsync, () => SelectedCount > 0, HandleUnexpectedError);
        SetThemeDefaultCommand = new AsyncCommand(() => SetThemeAsync("Default"), onError: HandleUnexpectedError);
        SetThemeLightCommand = new AsyncCommand(() => SetThemeAsync("Light"), onError: HandleUnexpectedError);
        SetThemeDarkCommand = new AsyncCommand(() => SetThemeAsync("Dark"), onError: HandleUnexpectedError);
        ToggleThemeCommand = new AsyncCommand(CycleThemeAsync, onError: HandleUnexpectedError);
        ToggleSettingsCommand = new AsyncCommand(ToggleSettingsAsync, onError: HandleUnexpectedError);

        UpdateConnectionTelemetry();
        UpdateQuotaMetrics();

        _browserSessions.Add(new BrowserAccountSession(_provider, _cacheService, _metricsStore, _statsScanner, _previewLoader, _imagePreviewLoader, _pdfPreviewLoader));

        // Every derived label on this view model reads through Loc at get time, so a language
        // change only has to tell the bindings to re-read (docs/PLAN-I18N.md §3). The view model
        // outlives the window, so there is nothing to unsubscribe from.
        Localizer.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>
    /// Registers a second (or later) account's browsing toolchain, so <see cref="SwitchBrowserAccountAsync"/>
    /// can switch to it live — P7 Phase B. Additive only, same shape as <c>SyncPanelViewModel.AddAccount</c>:
    /// existing callers/tests that never call this are completely unaffected.
    /// </summary>
    public void AddBrowsableAccount(
        ICloudDriveProvider provider,
        DriveCacheService cacheService,
        FolderMetricsStore? metricsStore = null,
        FolderStatsScanner? statsScanner = null,
        ITextFilePreviewLoader? previewLoader = null,
        IImageFilePreviewLoader? imagePreviewLoader = null,
        IPdfFilePreviewLoader? pdfPreviewLoader = null)
        => _browserSessions.Add(new BrowserAccountSession(provider, cacheService, metricsStore, statsScanner, previewLoader, imagePreviewLoader, pdfPreviewLoader));

    public ObservableCollection<DriveNodeViewModel> RootItems { get; }

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbItems { get; }

    /// <summary>
    /// The type filters worth offering for the folder on screen — one per kind actually present,
    /// plus "Todos". Rebuilt with the listing (docs/PLAN-BROWSER-VIEWS.md M6).
    /// </summary>
    public ObservableCollection<KindFilterViewModel> KindFilters { get; }

    public Sync.SyncPanelViewModel SyncPanel { get; }

    public LocalExplorerViewModel LocalExplorer { get; }

    /// <summary>
    /// The CLI's installed version and its self-update (docs/PLAN-UX-ROUND-4.md Z5 step 1). Its own
    /// view model: everything it does needs the release feed, the installer and somewhere to
    /// report, and nothing else here needs any of those.
    /// </summary>
    public CliUpdateViewModel CliUpdate { get; }

    public TransferQueueViewModel TransferQueue { get; } = new();

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

    public AsyncCommand ToggleLocalExplorerPanelCommand { get; }

    public AsyncCommand ToggleLogFilterCommand { get; }

    public AsyncCommand DownloadActivityCommand { get; }

    public AsyncCommand ClearActivityCommand { get; }

    public AsyncCommand ShowExplorerCommand { get; }

    /// <summary>Empties the folder search box (docs/PLAN-UX-ROUND-2.md §9).</summary>
    public AsyncCommand ClearSearchCommand { get; }

    /// <summary>Clears the search box and the kind chip together, from the empty state (docs/PLAN-UX-ROUND-3.md X3).</summary>
    public AsyncCommand ClearFiltersCommand { get; }

    /// <summary>Takes the alert strip down without resolving what it reported (docs/PLAN-UX-ROUND-3.md X1).</summary>
    public AsyncCommand DismissStatusBannerCommand { get; }

    public AsyncCommand ShowSettingsCommand { get; }

    /// <summary>Opens the sync pair list.</summary>
    public AsyncCommand ShowSyncCommand { get; }

    /// <summary>Opens the text viewer on the row currently selected in the listing.</summary>
    public AsyncCommand ViewSelectedFileCommand { get; }

    public AsyncCommand CloseViewerCommand { get; }

    /// <summary>Ctrl/Cmd+A over the listing — docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2.</summary>
    public AsyncCommand SelectAllRowsCommand { get; }

    /// <summary>Downloads every selected file (folders are skipped, same restriction as the single-row <see cref="DownloadItemAsync"/>) into one picked destination.</summary>
    public AsyncCommand DownloadSelectedCommand { get; }

    /// <summary>Moves every selected row (files and folders) to trash, after one confirmation for the whole batch.</summary>
    public AsyncCommand TrashSelectedCommand { get; }

    /// <summary>
    /// What `proton-drive --version` last reported, or why it could not be read. Shown as-is in the
    /// settings view; the CLI owns the wording, this view model does not reformat it.
    /// </summary>
    public AsyncCommand SwitchToProtonCommand { get; }

    public AsyncCommand SwitchToOneDriveCommand { get; }

    public AsyncCommand SwitchToGoogleDriveCommand { get; }

    public AsyncCommand SwitchToNextcloudCommand { get; }

    public AsyncCommand SwitchToS3Command { get; }

    public AsyncCommand SetThemeDefaultCommand { get; }

    public AsyncCommand SetThemeLightCommand { get; }

    public AsyncCommand SetThemeDarkCommand { get; }

    public AsyncCommand ToggleThemeCommand { get; }

    public AsyncCommand ToggleSettingsCommand { get; }

    public string ThemePreference
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                App.ApplyTheme(_theme);
                _settings.Update(s => s.Theme = _theme);
                OnPropertyChanged(nameof(IsSystemTheme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
            }
        }
    }

    public bool IsSystemTheme => string.Equals(_theme, "Default", StringComparison.OrdinalIgnoreCase);

    public bool IsLightTheme => string.Equals(_theme, "Light", StringComparison.OrdinalIgnoreCase);

    public bool IsDarkTheme => string.Equals(_theme, "Dark", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The languages the Settings picker offers. A fixed list from
    /// <see cref="LanguageCatalog"/> — adding one is a row there plus a locale file, and this
    /// property needs no change (docs/PLAN-I18N.md, Appendix B).
    /// </summary>
    public IReadOnlyList<Language> Languages => LanguageCatalog.Available;

    /// <summary>
    /// The interface language. Mirrors <see cref="ThemePreference"/> deliberately: apply first,
    /// then persist, so a failed write leaves the user looking at what they picked rather than
    /// silently reverting.
    /// </summary>
    public Language SelectedLanguage
    {
        get => Loc.Current;
        set
        {
            if (value is null || value.Code == Loc.Current.Code)
            {
                return;
            }

            Localizer.Instance.SetLanguage(value.Code);
            _settings.Update(s => s.Language = value.Code);
        }
    }

    /// <summary>
    /// The provider cards' sign-in/out tooltips. One key each, taking the provider's name, instead
    /// of a near-duplicate literal per provider — and the card these sit on is only visible while
    /// its own provider is active, so the active name is always the right one.
    /// </summary>
    public string SignInTooltip => Loc.F(StringKeys.Settings.SignInTooltip, ActiveProviderDisplayName);

    public string SignOutTooltip => Loc.F(StringKeys.Settings.SignOutTooltip, ActiveProviderDisplayName);

    public int BandwidthLimitKbps
    {
        get => _bandwidthLimitKbps;
        set
        {
            if (SetProperty(ref _bandwidthLimitKbps, value))
            {
                _settings.Update(s => s.BandwidthLimitKbps = value);
            }
        }
    }

    public string DefaultSyncFolder
    {
        get => _defaultSyncFolder;
        set
        {
            if (SetProperty(ref _defaultSyncFolder, value))
            {
                _settings.Update(s => s.DefaultSyncFolder = value);
            }
        }
    }

    public string ConnectionStatus => _connectionStatus;

    public string ConnectionStatusKind => _connectionStatusKind;

    public string ConnectionStatusDescription => _connectionStatusDescription;

    public bool IsOnline => _connectionStatusKind == "Online";

    public bool IsSyncing => _connectionStatusKind == "Syncing";

    public bool IsDisconnected => _connectionStatusKind == "Disconnected";

    public bool IsRateLimited => _connectionStatusKind == "RateLimited";

    /// <summary>
    /// Authenticated, but the last operation against the provider failed for a reason that makes
    /// the connection itself suspect. Exists so the header stops claiming "Online" in the same
    /// frame the body reports a failed load (docs/PLAN-UX-ROUND-2.md §2).
    /// </summary>
    public bool IsDegraded => _connectionStatusKind == "Degraded";

    /// <summary>
    /// True when the header badge is offering recovery rather than just reporting state — it
    /// becomes a button in exactly the two cases the user can do something about.
    /// </summary>
    public bool IsConnectionActionable => IsDisconnected || IsDegraded;

    /// <summary>
    /// Whether the standing warning has a remedy the app can offer. A warning the user cannot act
    /// on is a dead end, which is the whole point of U1 (docs/PLAN-UX-ROUND-2.md §1).
    /// </summary>
    public bool HasStatusAction => _status.HasAction;


    /// <summary>
    /// Whether the window-level alert strip is up (docs/PLAN-UX-ROUND-3.md X1). U1's recovery
    /// button lived inside the status panel, which is an optional preference and belongs to the
    /// explorer view — so a failure was invisible to anyone who had hidden the panel, and to
    /// everyone while they were in Settings or Sync. Only warnings get the strip: routine progress
    /// keeps the panel it has always used, because a banner for "Loaded 14 items" is noise.
    /// </summary>
    public bool IsStatusBannerVisible => _status.IsBannerVisible;

    /// <summary>
    /// The other half of the split: the status panel's card now carries progress and results only,
    /// so a warning is never rendered twice on the same screen.
    /// </summary>
    public bool IsInformationalStatus => _status.IsInformational;

    /// <summary>
    /// Which remedy, derived from the typed <see cref="DriveErrorKind"/> rather than from
    /// <see cref="StatusMessage"/>'s text (AGENTS.md: "Errors are typed").
    /// </summary>
    public string StatusActionLabel => Loc.T(NeedsReauthentication ? StringKeys.Status.ActionReconnect : StringKeys.Status.ActionRetry);

    public AsyncCommand StatusActionCommand => NeedsReauthentication ? AuthenticateCommand : RefreshCommand;

    private bool NeedsReauthentication => _lastErrorKind == DriveErrorKind.NotAuthenticated || !IsAuthenticated;

    public long QuotaUsedBytes => _quotaUsedBytes;

    /// <summary>
    /// Whether there is anything to show at all. Drives the whole gauge's visibility now, not just
    /// the bar inside it: with no usage figure there is nothing left to render, because the
    /// denominator is gone (docs/PLAN-UX-ROUND-4.md Y2).
    /// </summary>
    public bool IsQuotaUsageKnown => _quotaUsedIsKnown;

    /// <summary>
    /// The header gauge: what was measured, and nothing else.
    ///
    /// It used to read "— / 500 GB" — and that 500 GB was a per-provider constant, not the
    /// account's quota. A Proton free account is 5 GB; an S3 bucket has no quota at all, and was
    /// being told it had 5 TB. Round 2's U3 established that the app must not conflate "unknown"
    /// with a number, and fixed the used half of this very string while the total half went on
    /// asserting. There is no quota API on the provider seam yet, so the honest version of this
    /// gauge has no denominator, no percentage and no bar — all three were derived from the
    /// constant. When a provider can report a real total, it comes from the provider.
    /// </summary>
    public string QuotaDisplay
    {
        get
        {
            if (!_quotaUsedIsKnown)
            {
                return string.Empty;
            }

            var used = ByteSize.Format(_quotaUsedBytes);
            return _quotaUsedIsPartial
                ? Loc.F(StringKeys.Quota.UsedAtLeast, used)
                : Loc.F(StringKeys.Quota.Used, used);
        }
    }

    /// <summary>Explains what the figure above actually counted, and what is still not known.</summary>
    public string QuotaTooltip => _quotaUsedIsPartial
        ? Loc.T(StringKeys.Quota.TooltipPartial)
        : Loc.T(StringKeys.Quota.TooltipExact);

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

    /// <summary>
    /// A quick filter over the current folder's file/folder names (case-insensitive substring) —
    /// docs/INTERFACE_IMPROVEMENT_PLAN.md §2.1's "Global Quick Search". Combines with the kind
    /// filter chips rather than replacing them: both narrow the same underlying <see cref="_loadedItems"/>.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RenderItems();
                OnPropertyChanged(nameof(HasSearchText));
                OnPropertyChanged(nameof(SearchResultText));
                ClearSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether a search term is narrowing the listing, for the clear button. A filter that hides
    /// rows without saying so — and with no way back but selecting the text and deleting it — was
    /// the specific complaint (docs/PLAN-UX-ROUND-2.md §9).
    /// </summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>Whether the search box, a kind chip, or both are hiding rows right now.</summary>
    public bool HasActiveFilters => HasSearchText || _kindFilter is not null;

    /// <summary>
    /// Nothing on screen (docs/PLAN-UX-ROUND-3.md X3). Before this the pane simply went blank: an
    /// empty folder, a search that matched nothing and a filter that hid everything all rendered as
    /// the same empty rectangle, and the only wording for any of them lived in the metrics headline
    /// inside the optional status panel. Gated on a finished load and a live session so it cannot
    /// flash between "loading" and the first row arriving, or fight the sign-in card for the cell.
    /// </summary>
    public bool IsListingEmpty => _hasRenderedListing && IsAuthenticated && !IsLoading && RootItems.Count == 0;

    /// <summary>
    /// The folder does have contents — the filters are hiding all of them. A different situation
    /// from an empty folder, and the only one of the two with an action.
    /// </summary>
    public bool IsListingFilteredToNothing => IsListingEmpty && _loadedItems.Count > 0;

    public string ListingEmptyTitle => Loc.T(IsListingFilteredToNothing
        ? StringKeys.Explorer.EmptyFilteredTitle
        : StringKeys.Explorer.EmptyFolderTitle);

    public string ListingEmptyDetail => IsListingFilteredToNothing
        ? Loc.F(StringKeys.Explorer.EmptyFilteredDetail, _loadedItems.Count.ToString("n0", Loc.Culture))
        : Loc.T(StringKeys.Explorer.EmptyFolderDetail);

    /// <summary>
    /// How many rows survived the search, phrased the way the kind chips already phrase their own
    /// counts. Empty when nothing is being searched, so the label costs no space in the common case.
    /// </summary>
    public string SearchResultText
    {
        get
        {
            if (!HasSearchText)
            {
                return string.Empty;
            }

            return Loc.Plural(StringKeys.Explorer.SearchResults, RootItems.Count);
        }
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
                UpdateConnectionTelemetry();
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
    /// Which top-level view is on screen. One value rather than the pair of independent booleans
    /// this used to be: with a third view (sync, promoted out of the settings scroll in
    /// docs/PLAN-UX-ROUND-2.md §5) two booleans can represent "both" and "neither", neither of
    /// which is a screen. The viewer is deliberately not a member — it is an overlay on the
    /// explorer, not a sibling of it.
    /// </summary>
    public MainView ActiveView
    {
        get => _activeView;
        private set
        {
            if (SetProperty(ref _activeView, value))
            {
                OnPropertyChanged(nameof(IsExplorerView));
                OnPropertyChanged(nameof(IsSettingsView));
                OnPropertyChanged(nameof(IsSyncView));
            }
        }
    }

    public bool IsExplorerView => _activeView == MainView.Explorer;

    public bool IsSettingsView => _activeView == MainView.Settings;

    public bool IsSyncView => _activeView == MainView.Sync;

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
            await CliUpdate.CheckForCliUpdateCommand.ExecuteAsync();
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

    public Func<string, Task<bool>>? RequestConfirmationAsync { get; set; }

    public Func<string, Task>? RequestCopyToClipboardAsync { get; set; }

    public Func<string, IReadOnlyList<PropertyField>, Task>? RequestShowPropertiesAsync { get; set; }

    public string CliPath
    {
        get => _cliPath;
        set
        {
            if (SetProperty(ref _cliPath, value))
            {
                CliUpdate.Reset();
                PersistSettings();
                RaiseCommandStates();
            }
        }
    }

    public string OneDriveClientId
    {
        get => _oneDriveClientId;
        set
        {
            if (SetProperty(ref _oneDriveClientId, value))
            {
                _settings.Update(settings => settings.OneDriveClientId = value);
                RaiseCommandStates();
            }
        }
    }

    public string GoogleDriveClientId
    {
        get => _googleDriveClientId;
        set
        {
            if (SetProperty(ref _googleDriveClientId, value))
            {
                _settings.Update(settings => settings.GoogleDriveClientId = value);
                RaiseCommandStates();
            }
        }
    }

    public string GoogleDriveClientSecret
    {
        get => _googleDriveClientSecret;
        set
        {
            if (SetProperty(ref _googleDriveClientSecret, value))
            {
                _settings.Update(settings => settings.GoogleDriveClientSecret = value);
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
                RaiseEmptyStateChanged();
                UpdateConnectionTelemetry();
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
                RaiseEmptyStateChanged();
                UpdateConnectionTelemetry();
                RaiseProviderAuthStates();
            }
        }
    }

    /// <summary>
    /// The standing status line. Settable with a plain string for text that is already final
    /// (the view's own drag handlers do this); everything inside this view model goes through
    /// <see cref="SetStatus(LocalizedText)"/> instead, so the line can be re-rendered when the
    /// interface language changes rather than being frozen in the language it was written in
    /// (docs/PLAN-I18N.md §6.3).
    /// </summary>
    public string StatusMessage
    {
        get => _status.Message;
        set => SetStatus(LocalizedText.Verbatim(value));
    }

    /// <summary>The unrendered form, so tests can assert on a key instead of on prose.</summary>
    internal LocalizedText StatusText => _status.Text;

    private void SetStatus(LocalizedText text) => _status.Set(text);

    /// <summary>
    /// The two surfaces a status message can land on. Both are derived from
    /// <see cref="_isWarning"/> and <see cref="_statusMessage"/>, neither of which
    /// <see cref="SetProperty"/> can see on their behalf.
    /// </summary>
    /// <summary>
    /// The empty-state block's four derived properties. Their inputs are a collection's count and
    /// two flags in three different setters, none of which <see cref="SetProperty"/> can connect.
    /// </summary>
    private void RaiseEmptyStateChanged()
    {
        OnPropertyChanged(nameof(IsListingEmpty));
        OnPropertyChanged(nameof(IsListingFilteredToNothing));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ListingEmptyTitle));
        OnPropertyChanged(nameof(ListingEmptyDetail));
        ClearFiltersCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Announces everything <see cref="StatusSurface"/> feeds, after any change to it.</summary>
    private void RaiseStatusSurfaceChanged()
    {
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsStatusBannerVisible));
        OnPropertyChanged(nameof(IsInformationalStatus));
        OnPropertyChanged(nameof(HasStatusAction));
        OnPropertyChanged(nameof(StatusActionLabel));
        OnPropertyChanged(nameof(StatusActionCommand));
        DismissStatusBannerCommand?.RaiseCanExecuteChanged();
        UpdateConnectionTelemetry();
    }

    private async Task DismissStatusBannerAsync()
    {
        _status.Dismiss();
        await Task.CompletedTask;
    }

    private void SetStatus(string key, params object?[] args) => SetStatus(LocalizedText.Of(key, args));

    /// <summary>
    /// A provider operation failed: the message, the warning, and the kind that decides which
    /// remedy — if any — the alert strip offers. Kept as one call because the three were three
    /// statements at twenty call sites, and the third was simply missing
    /// (docs/PLAN-UX-ROUND-4.md Y3).
    /// </summary>
    private void SetFailure(LocalizedText text, Exception ex) => _status.Fail(text, ex);

    private void SetStatusPlural(string keyPrefix, int count, params object?[] args)
        => SetStatus(LocalizedText.Plural(keyPrefix, count, args));

    /// <summary>
    /// Re-renders the state that is stored rather than derived, after the interface language
    /// changes. Deliberately not routed through the normal setters: <see cref="SetStatus(LocalizedText)"/>
    /// clears <see cref="IsWarning"/>, and a language change must not make a standing warning
    /// disappear.
    /// </summary>
    private void OnLanguageChanged()
    {
        _status.Rerender();
        RefreshSelectionLabels();
        UpdateConnectionTelemetry();
        OnAllPropertiesChanged();

        // Six string properties were stored once and stayed in the language they were written in —
        // measured, not guessed (docs/PLAN-UX-ROUND-4.md Y7). Four of them are functions of current
        // state and can simply be recomputed here. The other two, CliVersion and CliUpdateStatus,
        // carry the result of a past operation and need a LocalizedText each; that is a change to
        // the self-update flow rather than to this method, and it is tracked rather than smuggled
        // in here.
        CliUpdate.OnLanguageChanged();
        Metrics.OnLanguageChanged();
        CommandConsoleToggleLabel = Loc.T(IsCommandConsoleVisible ? StringKeys.Console.ToggleHide : StringKeys.Console.ToggleShow);

        if (_activeOperationCount == 0)
        {
            ActiveCommand = Loc.T(StringKeys.Console.Idle);
        }

        if (!IsViewerVisible)
        {
            ViewerTitle = Loc.T(StringKeys.Viewer.Title);
        }

        // The console shows a placeholder when the buffer is empty and the buffer's own lines
        // otherwise; only the first is translatable.
        if (_commandLog.Lines.Count == 0)
        {
            CommandLogText = Loc.T(StringKeys.Console.NoCommandRunning);
        }
        else
        {
            RefreshCommandLogText();
        }

        // Every child that is its own binding source has to be told separately: the notification
        // above reaches bindings whose source is this view model, and a chip's LabelWithCount and
        // a row's tooltips are bound against the chip and the row. Switching to Spanish used to
        // leave "All (14) Folders (8)" over a fully translated toolbar until something re-listed
        // the folder (docs/PLAN-UX-ROUND-3.md X8).
        foreach (var chip in KindFilters)
        {
            chip.RefreshLocalizedText();
        }

        foreach (var node in RootItems)
        {
            node.RefreshLocalizedText();
        }
    }

    public bool IsWarning
    {
        get => _status.IsWarning;
        private set
        {
            if (value)
            {
                _status.Warn();
            }
            else
            {
                _status.ClearWarning();
            }
        }
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

    /// <summary>
    /// How many CLI/Graph operations are currently mid-flight, across every active provider
    /// session — a real count derived from Started/Finished pairs in <see cref="OnActivity"/>,
    /// unlike <see cref="ActiveCommand"/> (a single label that Task 4's own comment on
    /// <see cref="OnActivity"/> notes can't represent two concurrent operations correctly). Shown
    /// in the floating status line while the console is collapsed.
    /// </summary>
    public int ActiveOperationCount
    {
        get => _activeOperationCount;
        private set
        {
            if (SetProperty(ref _activeOperationCount, value))
            {
                OnPropertyChanged(nameof(ActiveOperationsText));
            }
        }
    }

    /// <summary>
    /// The floating status line's count. Was a <c>StringFormat</c> in the markup reading
    /// "{0} operación(es) activa(s)" — a Spanish-specific plural hack that no other language can
    /// reproduce, and which a XAML format string has no way to express. Plural selection belongs
    /// here (docs/PLAN-I18N.md §5).
    /// </summary>
    public string ActiveOperationsText => Loc.Plural(StringKeys.Console.ActiveOperations, ActiveOperationCount);

    /// <summary>The most recent line added to the log, regardless of the search/warnings filter below — always the real last event, not whatever the filter happens to be hiding.</summary>
    public string? LastLogLine
    {
        get => _lastLogLine;
        private set => SetProperty(ref _lastLogLine, value);
    }

    /// <summary>Task 4's "Filter: Toggle error/warning-only log views" — matches lines carrying this app's own <c>[warn]</c>/<c>[err]</c>/<c>[fail]</c> markers (see <see cref="OnActivity"/>/<see cref="HandleUnexpectedError"/>).</summary>
    public bool ShowOnlyWarningsAndErrors
    {
        get => _showOnlyWarningsAndErrors;
        set
        {
            if (SetProperty(ref _showOnlyWarningsAndErrors, value))
            {
                RefreshCommandLogText();
            }
        }
    }

    /// <summary>Task 4's log search input — a live, case-insensitive substring filter over the buffered lines.</summary>
    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetProperty(ref _logSearchText, value))
            {
                RefreshCommandLogText();
            }
        }
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

    /// <summary>
    /// Whether the in-app text viewer is open over the listing. The viewer is a panel and not a
    /// separate window so it can't get lost behind the main one, and so closing it needs no
    /// window plumbing in code-behind.
    /// </summary>
    public bool IsViewerVisible
    {
        get => _isViewerVisible;
        private set => SetProperty(ref _isViewerVisible, value);
    }

    /// <summary>True while the file is being downloaded for the viewer — a preview costs a real CLI download.</summary>
    public bool IsViewerLoading
    {
        get => _isViewerLoading;
        private set => SetProperty(ref _isViewerLoading, value);
    }

    /// <summary>The previewed file's name, shown as the viewer's heading.</summary>
    public string ViewerTitle
    {
        get => _viewerTitle;
        private set => SetProperty(ref _viewerTitle, value);
    }

    /// <summary>The previewed file's remote path, shown under the heading.</summary>
    public string ViewerPath
    {
        get => _viewerPath;
        private set => SetProperty(ref _viewerPath, value);
    }

    /// <summary>The text on screen. Empty while loading, and for a file that turned out to be binary.</summary>
    public string ViewerText
    {
        get => _viewerText;
        private set
        {
            if (SetProperty(ref _viewerText, value))
            {
                OnPropertyChanged(nameof(HasViewerText));
            }
        }
    }

    /// <summary>
    /// The line under the viewer's toolbar: size, encoding, and — when it applies — that what's on
    /// screen is only the beginning of the file. Never silently truncate.
    /// </summary>
    public string ViewerNote
    {
        get => _viewerNote;
        private set => SetProperty(ref _viewerNote, value);
    }

    public bool HasViewerText => _viewerText.Length > 0;

    /// <summary>
    /// The previewed image's raw bytes, undecoded — decoding is a view concern (view models never
    /// touch Avalonia types, AGENTS.md), so the view turns this into a <c>Bitmap</c> via
    /// <c>Views.Converters.BytesToBitmapConverter</c>.
    /// </summary>
    public byte[]? ViewerImageBytes
    {
        get => _viewerImageBytes;
        private set
        {
            if (SetProperty(ref _viewerImageBytes, value))
            {
                OnPropertyChanged(nameof(HasViewerImage));
                OnPropertyChanged(nameof(HasViewerZoomableContent));
            }
        }
    }

    public bool HasViewerImage => _viewerImageBytes is { Length: > 0 };

    /// <summary>
    /// One PNG-encoded bitmap per rendered PDF page, undecoded for the same reason as
    /// <see cref="ViewerImageBytes"/> — the View decodes each entry with the same
    /// <c>BytesToBitmapConverter</c>.
    /// </summary>
    public IReadOnlyList<byte[]>? ViewerPdfPages
    {
        get => _viewerPdfPages;
        private set
        {
            if (SetProperty(ref _viewerPdfPages, value))
            {
                OnPropertyChanged(nameof(HasViewerPdf));
                OnPropertyChanged(nameof(HasViewerZoomableContent));
            }
        }
    }

    public bool HasViewerPdf => _viewerPdfPages is { Count: > 0 };

    /// <summary>Whether the zoom control has anything to act on — hidden for the text viewer, which sizes by font instead.</summary>
    public bool HasViewerZoomableContent => HasViewerImage || HasViewerPdf;

    /// <summary>
    /// The image/PDF viewer's display scale — see <see cref="AppSettings.ViewerZoom"/> for why the
    /// default isn't 1.0. Clamped the same way on every write, not just on load, since the slider
    /// itself is already range-limited but a value set some other way (a future keyboard shortcut,
    /// say) shouldn't be able to hand the view something degenerate.
    /// </summary>
    public double ViewerZoom
    {
        get => _viewerZoom;
        set
        {
            // Not persisted here: AppSettingsService.Update reads settings.json and writes it
            // back, and a slider raises this on every intermediate value of a drag
            // (docs/PLAN-UX-ROUND-4.md Y6). The view commits it when the drag ends, and closing the
            // viewer commits it too, so a zoom set with the keyboard is not lost either.
            SetProperty(ref _viewerZoom, Math.Clamp(value, AppSettings.MinViewerZoom, AppSettings.MaxViewerZoom));
        }
    }

    /// <summary>Whether the Status panel's per-item fields (as opposed to the current-folder ones) have anything to show.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetProperty(ref _hasSelection, value);
    }

    /// <summary>docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2 — how many rows Ctrl/Shift-click or Ctrl+A currently have marked.</summary>
    public int SelectedCount => RootItems.Count(node => node.IsSelected);

    /// <summary>Whether the Status panel should show the multi-select summary instead of one item's details.</summary>
    public bool HasMultipleSelected => SelectedCount > 1;

    /// <summary>Whether the Status panel should show the single-item details block.</summary>
    public bool IsSingleSelected => SelectedCount == 1;

    public string SelectionSummaryText => SelectedCount == 0
        ? string.Empty
        : Loc.Plural(StringKeys.Explorer.SelectionCount, SelectedCount);

    public bool IsCommandConsoleVisible
    {
        get => _isCommandConsoleVisible;
        private set
        {
            if (SetProperty(ref _isCommandConsoleVisible, value))
            {
                CommandConsoleMaxHeight = value ? _commandConsoleHeight + ConsoleChromeHeight : 0;
                CommandConsoleOpacity = value ? 1 : 0;
                CommandConsoleHitTestVisible = value;
                CommandConsoleToggleLabel = Loc.T(value ? StringKeys.Console.ToggleHide : StringKeys.Console.ToggleShow);
                CommandConsoleToggleGlyph = value ? "▼" : "▲";
                _settings.Update(s => s.ShowCommandConsole = value);
                RaiseCommandStates();
            }
        }
    }

    /// <summary>
    /// What the console's own padding and border add on top of the scrolling body, so the collapse
    /// animation's MaxHeight and the dragged body height stay in step.
    /// </summary>
    private const double ConsoleChromeHeight = 40;

    /// <summary>
    /// The console body's height, dragged by the handle above it (docs/PLAN-UX-ROUND-3.md X7).
    /// Round 1's Task 4 asked for this and only the collapse toggle shipped; the body has been a
    /// hard-coded 140px ever since. Persisted, so the size the user leaves it at is next launch's.
    /// </summary>
    public double CommandConsoleHeight
    {
        get => _commandConsoleHeight;
        private set
        {
            var clamped = Math.Clamp(value, AppSettings.MinCommandConsoleHeight, AppSettings.MaxCommandConsoleHeight);
            if (SetProperty(ref _commandConsoleHeight, clamped))
            {
                if (IsCommandConsoleVisible)
                {
                    CommandConsoleMaxHeight = clamped + ConsoleChromeHeight;
                }
            }
        }
    }

    /// <summary>
    /// Applies one drag step. Dragging the handle up makes the console taller, so the delta is
    /// subtracted — the view passes a raw pointer delta and this owns the direction and the limits.
    /// </summary>
    public void ResizeCommandConsole(double verticalDelta) => CommandConsoleHeight -= verticalDelta;

    /// <summary>
    /// Writes the dragged height, once, when the drag ends. Not from the setter:
    /// <see cref="AppSettingsService.Update"/> reads settings.json and writes it back, and the
    /// setter runs on every pointer move — a single drag across the console would have been a
    /// hundred read-modify-write cycles on the user's config file
    /// (docs/PLAN-UX-ROUND-4.md Y6).
    /// </summary>
    public void CommitCommandConsoleHeight()
        => _settings.Update(s => s.CommandConsoleHeight = _commandConsoleHeight);

    /// <summary>Writes the zoom once, when the gesture that changed it ends. See <see cref="ViewerZoom"/>.</summary>
    public void CommitViewerZoom()
        => _settings.Update(s => s.ViewerZoom = _viewerZoom);

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

    /// <summary>
    /// Whether the right-hand Status/Metrics sidebar is shown. Persisted: the value the user last
    /// left it in is also what the next launch starts with, same as <see cref="ShowLocalExplorerPanel"/>-backed
    /// <see cref="IsLocalExplorerPanelVisible"/> below.
    /// </summary>
    /// <summary>A "User Settings" checkbox in the settings view, not a header button — reads/writes
    /// directly rather than through a command, the way <see cref="DefaultSyncFolder"/> and
    /// <see cref="BandwidthLimitKbps"/> (both plain two-way-bound settings-view fields) already do.</summary>
    public bool IsStatusPanelVisible
    {
        get => _isStatusPanelVisible;
        set
        {
            if (SetProperty(ref _isStatusPanelVisible, value))
            {
                _settings.Update(s => s.ShowStatusPanel = value);
            }
        }
    }

    /// <summary>Whether the local filesystem pane (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 3) is expanded.</summary>
    public bool IsLocalExplorerPanelVisible
    {
        get => _isLocalExplorerPanelVisible;
        private set
        {
            if (SetProperty(ref _isLocalExplorerPanelVisible, value))
            {
                _settings.Update(s => s.ShowLocalExplorerPanel = value);
            }
        }
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

        await LocalExplorer.InitializeAsync();

        var needsCliPath = _provider.Id == ProviderId.Proton;
        if ((!needsCliPath || !string.IsNullOrWhiteSpace(CliPath)) && IsAuthenticated)
        {
            await GoToRootAsync();
            return;
        }

        SetStatus(needsCliPath && string.IsNullOrWhiteSpace(CliPath)
            ? LocalizedText.Of(StringKeys.Status.PickCli, _provider.DisplayName)
            : LocalizedText.Of(StringKeys.Status.SignInToLoad, RootPath));
    }

    private bool CanAuthenticate() => !IsLoading && !IsAuthenticated && _provider.Id switch
    {
        ProviderId.OneDrive => !string.IsNullOrWhiteSpace(OneDriveClientId),
        ProviderId.GoogleDrive => !string.IsNullOrWhiteSpace(GoogleDriveClientId),
        ProviderId.Proton => !string.IsNullOrWhiteSpace(CliPath),
        _ => true,
    };

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
        // Disposed, not just dropped: a replaced CancellationTokenSource keeps its registrations
        // and its timer alive until finalization, and this one is replaced per scan
        // (docs/PLAN-UX-ROUND-4.md Z2). BeginPreview already got this right.
        _deepScanCts?.Dispose();
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

            SetStatusPlural(
                metrics.IsComplete ? StringKeys.Status.ScanDone : StringKeys.Status.ScanCancelled,
                metrics.ScannedFolderCount,
                path);
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(path, ex), ex);
        }
        catch (DbException ex)
        {
            // The scan itself succeeded; only storing it failed. Say so rather than implying the
            // minutes were wasted.
            SetStatus(StringKeys.Status.ScanSaveFailed, path, ex.Message);
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
            SetStatus(StringKeys.Status.AuthOpening, _provider.DisplayName);
            await _provider.Auth.AuthenticateAsync();
            IsAuthenticated = true;
            _settings.Update(settings =>
            {
                settings.SetProviderAuthenticated(_provider.Id, true);
                var liveLabel = _provider switch
                {
                    OneDriveProvider { Auth: GraphAuthenticator { AccountLabel: { } oneDriveLabel } } => oneDriveLabel,
                    GoogleDriveProvider { Auth: GoogleDriveAuthenticator { AccountLabel: { } googleDriveLabel } } => googleDriveLabel,
                    GenericCloudDriveProvider { AccountIdentity: { } identity } => identity,
                    _ => null
                };
                if (liveLabel is not null)
                {
                    settings.SetProviderAccountLabel(_provider.Id, liveLabel);
                }
            });
            UpdateConnectionTelemetry();
            UpdateQuotaMetrics();
            RefreshAvailableProviders();
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(SelectedProviderIndex));
            OnPropertyChanged(nameof(OneDriveAccountLabel));
            OnPropertyChanged(nameof(GoogleDriveAccountLabel));
            await GoToRootAsync();
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError("auth login", ex), ex);
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
            SetStatus(StringKeys.Status.AuthSigningOut, _provider.DisplayName);
            await _provider.Auth.LogoutAsync();
            IsAuthenticated = false;
            _settings.Update(settings => settings.SetProviderAuthenticated(_provider.Id, false));
            UpdateConnectionTelemetry();
            UpdateQuotaMetrics();
            RefreshAvailableProviders();
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(SelectedProviderIndex));
            OnPropertyChanged(nameof(OneDriveAccountLabel));
            OnPropertyChanged(nameof(GoogleDriveAccountLabel));
            ResetBrowserState();
            SetStatus(StringKeys.Status.AuthSignedOut, _provider.DisplayName);
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError("auth logout", ex), ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// <see cref="SwitchBrowserAccountAsync"/>, fire-and-forget from a property setter (the header
    /// ComboBox's <c>SelectedItem</c> binding has nowhere to await a <see cref="Task"/>) but still
    /// routed through <see cref="HandleUnexpectedError"/> like every other command, instead of
    /// leaving a thrown exception unobserved and the UI silently stuck mid-switch.
    /// </summary>
    private async Task SwitchProviderAndReportErrorsAsync(ProviderId id)
    {
        try
        {
            await SwitchBrowserAccountAsync(id);
        }
        catch (Exception ex)
        {
            HandleUnexpectedError(ex);
        }
    }

    /// <summary>
    /// Switches which registered <see cref="BrowserAccountSession"/> the browser shows — live, no
    /// restart (P7 Phase B, docs/PLAN-CLOUD-PROVIDERS.md; §2.7's original "requires a restart" note
    /// only ever reflected that this hadn't been built yet, not a hard architectural limit). A
    /// no-op if <paramref name="id"/> isn't registered (not authenticated/configured, or simply not
    /// one of <see cref="_browserSessions"/> yet).
    /// </summary>
    public async Task SwitchBrowserAccountAsync(ProviderId id)
    {
        if (id == _provider.Id)
        {
            return;
        }

        _isSwitchingProvider = true;
        try
        {
            await SwitchBrowserAccountCoreAsync(id);
        }
        finally
        {
            // Cleared before the final raise, so this last push is the one the control accepts and
            // echoes back harmlessly — every echo before it was ignored as re-entrant.
            _isSwitchingProvider = false;
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(SelectedProviderIndex));
        }
    }

    private async Task SwitchBrowserAccountCoreAsync(ProviderId id)
    {
        var session = _browserSessions.FirstOrDefault(candidate => candidate.Provider.Id == id);
        if (session is null)
        {
            SetStatus(StringKeys.Status.ProviderNotConfigured, id);
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(SelectedProviderIndex));
            return;
        }

        // Whatever the previous account's browser was mid-loading, it no longer belongs on screen
        // once the account underneath it has changed.
        _cts?.Cancel();

        _provider = session.Provider;
        _cacheService = session.CacheService;
        _metricsStore = session.MetricsStore;
        _statsScanner = session.StatsScanner;
        _previewLoader = session.PreviewLoader;
        _imagePreviewLoader = session.ImagePreviewLoader;
        _pdfPreviewLoader = session.PdfPreviewLoader;
        _rootPath = _provider.Id == ProviderId.Proton ? "/my-files" : "/";

        // Both of these used to be wired once at startup and never revisited — harmless before
        // this phase, since the browsed account never changed. Left stale, "Add pair"'s remote
        // folder browser would list the *previous* account's tree starting from the *new*
        // account's root path (a real bug: navigating OneDrive, switching to Proton, then
        // browsing for a remote folder to sync showed OneDrive's listing under a Proton-shaped
        // path, mixing the two).
        SyncPanel.GetRemoteFolderChildren = _provider.Operations.ListFolderAsync;
        SyncPanel.SetActiveAccount(_provider.DisplayName);
        
        // Re-read fresh rather than trust the field left over from the previous account — it can
        // otherwise go stale the moment auth changes for the account not currently on screen.
        var currentSettings = _settings.Load();
        IsAuthenticated = currentSettings.IsProviderAuthenticated(_provider.Id);

        // A deep-scan histogram belongs to one specific folder on one specific account — carrying
        // it over to a different account's (unrelated) folder would show buckets for content that
        // isn't even on screen.
        KindFilters.Clear();
        _kindFilter = null;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        FilterSummary = string.Empty;

        UpdateQuotaMetrics();
        UpdateConnectionTelemetry();

        RefreshAvailableProviders();
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderIndex));
        OnPropertyChanged(nameof(ActiveProviderDisplayName));
        OnPropertyChanged(nameof(BrowserHeaderTitle));
        OnPropertyChanged(nameof(BrowserHeaderSubtitle));
        OnPropertyChanged(nameof(IsProtonActive));
        OnPropertyChanged(nameof(IsOneDriveActive));
        OnPropertyChanged(nameof(IsGoogleDriveActive));
        OnPropertyChanged(nameof(IsNextcloudActive));
        OnPropertyChanged(nameof(IsS3Active));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(OneDriveAccountLabel));
        OnPropertyChanged(nameof(GoogleDriveAccountLabel));
        OnPropertyChanged(nameof(RootPath));
        RaiseCommandStates();

        _settings.Update(settings => settings.ActiveProvider = id.ToString());

        if (!IsAuthenticated)
        {
            SetStatus(StringKeys.Status.ProviderNeedsAuth, _provider.DisplayName);
            ResetBrowserState();
            return;
        }

        SetStatus(StringKeys.Status.ProviderSwitched, _provider.DisplayName);

        try
        {
            await GoToRootAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DriveException)
        {
            IsAuthenticated = false;
            UpdateConnectionTelemetry();
            SetStatus(StringKeys.Status.ProviderNeedsAuth, _provider.DisplayName);
            ResetBrowserState();
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
            SetFailure(FormatDriveError(previousPath, ex), ex);
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

            SetFailure(FormatDriveError(path, ex), ex);
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
            SetFailure(FormatDriveError(CurrentPath, ex), ex);
        }
    }

    private async Task UploadAsync()
    {
        var picker = RequestUploadFilesAsync;
        if (picker is null)
        {
            SetStatus(StringKeys.Status.UploadUnavailable);
            return;
        }

        var files = await picker();
        if (files.Count == 0)
        {
            return;
        }

        var strategy = await ResolveUploadConflictStrategyAsync(files, CurrentPath);
        if (strategy is null)
        {
            SetStatus(StringKeys.Status.UploadCancelled);
            return;
        }

        try
        {
            IsLoading = true;
            SetStatusPlural(StringKeys.Status.UploadProgress, files.Count, CurrentPath);
            await _provider.Operations.UploadFilesAsync(files, CurrentPath, strategy.Value);
            SetStatusPlural(StringKeys.Status.UploadDone, files.Count, CurrentPath);
            await InvalidateDeepMetricsAsync(CurrentPath);

            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(CurrentPath, ex), ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Shared by <see cref="UploadAsync"/> and drag-and-drop uploads (<see
    /// cref="HandleLocalFilesDroppedAsync"/>): checks <paramref name="localPaths"/>' names against
    /// what's already at <paramref name="targetPath"/>, and prompts once for the whole batch via
    /// <see cref="RequestConflictStrategyAsync"/> if any collide. Returns null if the user cancelled
    /// the prompt (distinct from <see cref="UploadConflictStrategy.None"/>, which also means "no
    /// conflicts to resolve" when nothing collided in the first place).
    ///
    /// The conflict check itself only covers <paramref name="targetPath"/> when it's the folder
    /// already loaded in memory (<see cref="RootItems"/>) — a drop onto a different folder row skips
    /// it rather than paying for an extra listing call mid-drag; the CLI's own default handling
    /// still applies there.
    /// </summary>
    private async Task<UploadConflictStrategy?> ResolveUploadConflictStrategyAsync(IReadOnlyList<string> localPaths, string targetPath)
    {
        if (RequestConflictStrategyAsync is null || !string.Equals(targetPath, CurrentPath, StringComparison.Ordinal))
        {
            return UploadConflictStrategy.None;
        }

        var remoteFileNames = RootItems.Select(ni => ni.Item.Name).ToHashSet();
        var conflictingFiles = localPaths
            .Select(Path.GetFileName)
            .Where(name => name is not null && remoteFileNames.Contains(name))
            .ToList();

        if (conflictingFiles.Count == 0)
        {
            return UploadConflictStrategy.None;
        }

        var strategy = await RequestConflictStrategyAsync(conflictingFiles!);
        return strategy == UploadConflictStrategy.None ? null : strategy;
    }

    /// <summary>
    /// A local pane row (or rows) dropped onto the cloud pane (docs/INTERFACE_IMPROVEMENT_PLAN.md
    /// Task 5, Phase 2) — the code-behind drag/drop handlers translate the gesture into this call
    /// and do nothing else, per the MVVM rule that view-model business logic stays out of
    /// code-behind. Routes through <see cref="TransferQueue"/> rather than blocking on
    /// <see cref="IsLoading"/> the way the toolbar's own <see cref="UploadCommand"/> does — a drag
    /// shouldn't freeze every other <c>IsLoading</c>-gated control in the window.
    /// </summary>
    public async Task HandleLocalFilesDroppedAsync(IReadOnlyList<string> localPaths, string targetPath)
    {
        if (localPaths.Count == 0)
        {
            return;
        }

        var strategy = await ResolveUploadConflictStrategyAsync(localPaths, targetPath);
        if (strategy is null)
        {
            SetStatus(StringKeys.Status.UploadCancelled);
            return;
        }

        var result = await TransferQueue.EnqueueUpload(_provider.Operations, localPaths, targetPath, strategy.Value);

        // Set before kicking off the refresh below, not after: that refresh's own transient
        // "Loading.../Showing cached items..." messages would otherwise immediately overwrite this
        // one. It's still the last word for a moment, and the refresh's own eventual "Loaded N
        // items..." is itself a second, later confirmation once it lands.
        var batch = DescribeBatchForStatus(localPaths);
        SetStatus(result.Status switch
        {
            TransferStatus.Done => LocalizedText.Of(StringKeys.Status.UploadDoneItem, batch, targetPath),
            TransferStatus.Failed => LocalizedText.Of(StringKeys.Status.UploadFailedItem, batch, result.ErrorMessage),
            _ => LocalizedText.Of(StringKeys.Status.UploadCancelledItem, batch),
        });

        // Fire-and-forget, same as the toolbar's own UploadAsync: RefreshAsync ends in a
        // Dispatcher.UIThread.InvokeAsync post that only completes with a running Avalonia
        // dispatcher, so awaiting it deadlocks headless callers (including every xUnit test).
        if (result.Status == TransferStatus.Done && string.Equals(targetPath, CurrentPath, StringComparison.Ordinal))
        {
            _ = RefreshAsync();
        }
    }

    private static string DescribeBatchForStatus(IReadOnlyList<string> localPaths)
        => localPaths.Count == 1 ? Path.GetFileName(localPaths[0]) : $"{localPaths.Count} elementos";

    /// <summary>
    /// A cloud pane row (or rows) dropped onto the local pane (docs/INTERFACE_IMPROVEMENT_PLAN.md
    /// Task 5, Phase 3) — mirrors <see cref="HandleLocalFilesDroppedAsync"/> for the other
    /// direction. Files and folders both download (`filesystem download` is recursive for folders,
    /// verified in docs/PLAN-LOCAL-SYNC.md — the row's own manual <c>DownloadCommand</c> disables
    /// folders for an unrelated, app-level reason, see docs/ARCHITECTURE.md §9 item 11).
    ///
    /// Unlike upload, the download operation itself has no conflict-strategy parameter — the CLI's
    /// `filesystem download` command has no equivalent to upload's <c>-f</c>/<c>-d</c> flags. So
    /// "Skip" is the only choice this method can actually honor beyond a plain download: it drops
    /// the conflicting items from the batch. "Replace" and "Keep Both" both fall through to a plain
    /// download — there is no verified way to make the CLI (or this app, without downloading to a
    /// temp path and renaming after, which isn't built) rename the incoming file instead of
    /// whatever the CLI itself does when the target name already exists.
    /// </summary>
    public async Task HandleCloudItemsDroppedAsync(IReadOnlyList<DriveItem> items, string targetLocalPath)
    {
        if (items.Count == 0)
        {
            return;
        }

        var toDownload = items;
        if (RequestConflictStrategyAsync is not null)
        {
            var conflictingNames = items
                .Where(item => _localFileSystem.Exists(Path.Combine(targetLocalPath, item.Name)))
                .Select(item => item.Name)
                .ToList();

            if (conflictingNames.Count > 0)
            {
                var strategy = await RequestConflictStrategyAsync(conflictingNames);
                if (strategy == UploadConflictStrategy.None)
                {
                    SetStatus(StringKeys.Status.DownloadCancelled);
                    return;
                }

                if (strategy == UploadConflictStrategy.Skip)
                {
                    var skip = conflictingNames.ToHashSet();
                    toDownload = items.Where(item => !skip.Contains(item.Name)).ToList();
                }
            }
        }

        var results = new List<TransferItemViewModel>(toDownload.Count);
        foreach (var item in toDownload)
        {
            results.Add(await TransferQueue.EnqueueDownload(_provider.Operations, item, targetLocalPath));
        }

        // Safe to await, unlike the upload side's cloud-pane RefreshAsync (see its comment):
        // LocalExplorerViewModel.NavigateAsync never touches Dispatcher.UIThread, so it can't
        // deadlock a headless caller the same way.
        var anyDone = results.Any(r => r.Status == TransferStatus.Done);
        if (anyDone && string.Equals(targetLocalPath, LocalExplorer.CurrentPath, StringComparison.Ordinal))
        {
            await LocalExplorer.RefreshCommand.ExecuteAsync();
        }

        var failed = results.Where(r => r.Status == TransferStatus.Failed).ToList();
        var done = results.Count(r => r.Status == TransferStatus.Done);
        SetStatus(failed.Count switch
        {
            0 when done > 0 => done == 1
                ? LocalizedText.Of(StringKeys.Status.DownloadDoneTo, toDownload[0].Name, targetLocalPath)
                : LocalizedText.Plural(StringKeys.Status.DownloadDone, done, targetLocalPath),
            0 => LocalizedText.Of(StringKeys.Status.DownloadCancelledTo, targetLocalPath),
            _ => LocalizedText.Of(StringKeys.Status.DownloadFailed, failed.Count, results.Count, failed[0].ErrorMessage),
        });
    }

    private async Task CreateFolderAsync()
    {
        var requester = RequestCreateFolderAsync;
        if (requester is null)
        {
            SetStatus(StringKeys.Status.NewFolderUnavailable);
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
            SetStatus(StringKeys.Status.NewFolderProgress, folderName, CurrentPath);
            await _provider.Operations.CreateFolderAsync(CurrentPath, folderName);
            SetStatus(StringKeys.Status.NewFolderDone, folderName, CurrentPath);
            
            // Update DB immediately
            var newFolderPath = _provider.Paths.Combine(CurrentPath, folderName);
            await _cacheService.AddOrUpdateItemAsync(CurrentPath, new DriveItem(newFolderPath, folderName, true));
            await InvalidateDeepMetricsAsync(newFolderPath);

            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(CurrentPath, ex), ex);
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
            SetStatus(StringKeys.Status.DownloadUnavailable);
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
            SetStatus(StringKeys.Status.DownloadProgress, item.Name);
            await _provider.Operations.DownloadFileAsync(item.Path, folder);
            SetStatus(StringKeys.Status.DownloadDoneTo, item.Name, folder);
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(item.Path, ex), ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Opens the viewer on <paramref name="item"/> — as text or as an image, whichever
    /// <see cref="ImagePreviewPolicy"/>/<see cref="TextPreviewPolicy"/> say it is. Images are
    /// checked first: an image's <see cref="FileKind"/> never also qualifies as text, so the order
    /// only matters for the refusal message when neither policy accepts the file.
    /// </summary>
    public async Task PreviewItemAsync(DriveItem item)
    {
        if (!item.IsFolder && FileKindClassifier.Classify(item.Name, isFolder: false) == FileKind.Image && ImagePreviewPolicy.CanPreview(item))
        {
            await PreviewImageAsync(item);
            return;
        }

        if (PdfPreviewPolicy.CanPreview(item))
        {
            await PreviewPdfAsync(item);
            return;
        }

        if (TextPreviewPolicy.CanPreview(item))
        {
            await PreviewTextAsync(item);
            return;
        }

        SetStatus(
            StringKeys.Status.ViewerUnsupported,
            item.Name,
            TextPreviewPolicy.MaxPreviewBytes / 1024,
            ImagePreviewPolicy.MaxPreviewBytes / (1024 * 1024),
            PdfPreviewPolicy.MaxPreviewBytes / (1024 * 1024));
        IsWarning = true;
    }

    /// <summary>
    /// The text half of <see cref="PreviewItemAsync"/>. The CLI can only download, so this pays for
    /// a real download of the file into a temp folder that the loader deletes again.
    /// </summary>
    private async Task PreviewTextAsync(DriveItem item)
    {
        if (_previewLoader is null)
        {
            SetStatus(StringKeys.Status.ViewerTextUnavailable);
            IsWarning = true;
            return;
        }

        var cts = BeginPreview(item);

        try
        {
            var preview = await _previewLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (preview.IsBinary)
            {
                ViewerText = string.Empty;
                ViewerNote = Loc.F(StringKeys.Viewer.NotAText, preview.ByteCount.ToString("n0", Loc.Culture));
                SetStatus(StringKeys.Status.ViewerNotAText, item.Name);
                IsWarning = true;
                return;
            }

            ViewerText = preview.Text;
            ViewerNote = FormatViewerNote(preview);
            SetStatus(StringKeys.Status.ViewerShowing, item.Name);
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed; whoever did that already owns the panel's state.
        }
        catch (InvalidOperationException ex)
        {
            ViewerNote = Loc.T(StringKeys.Status.ViewerOpenFailed);
            SetFailure(FormatDriveError(item.Path, ex), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewerNote = Loc.T(StringKeys.Status.ViewerReadFailed);
            SetStatus(StringKeys.Status.ViewerError, item.Name, ex.DescribeForUser().Render());
            IsWarning = true;
        }
        finally
        {
            EndPreview(cts);
        }
    }

    /// <summary>The image half of <see cref="PreviewItemAsync"/> — same download-then-show shape as the text one.</summary>
    private async Task PreviewImageAsync(DriveItem item)
    {
        if (_imagePreviewLoader is null)
        {
            SetStatus(StringKeys.Status.ViewerImageUnavailable);
            IsWarning = true;
            return;
        }

        var cts = BeginPreview(item);

        try
        {
            var preview = await _imagePreviewLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ViewerImageBytes = preview.Bytes;
            ViewerNote = Loc.F(StringKeys.Viewer.NoteBytes, preview.ByteCount.ToString("n0", Loc.Culture));
            SetStatus(StringKeys.Status.ViewerShowing, item.Name);
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed; whoever did that already owns the panel's state.
        }
        catch (InvalidOperationException ex)
        {
            ViewerNote = Loc.T(StringKeys.Status.ViewerOpenFailed);
            SetFailure(FormatDriveError(item.Path, ex), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewerNote = Loc.T(StringKeys.Status.ViewerReadFailed);
            SetStatus(StringKeys.Status.ViewerError, item.Name, ex.DescribeForUser().Render());
            IsWarning = true;
        }
        finally
        {
            EndPreview(cts);
        }
    }

    /// <summary>The PDF half of <see cref="PreviewItemAsync"/> — same download-then-show shape, plus rendering the pages the loader already did.</summary>
    private async Task PreviewPdfAsync(DriveItem item)
    {
        if (_pdfPreviewLoader is null)
        {
            SetStatus(StringKeys.Status.ViewerPdfUnavailable);
            IsWarning = true;
            return;
        }

        var cts = BeginPreview(item);

        try
        {
            var preview = await _pdfPreviewLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ViewerPdfPages = preview.Pages;
            ViewerNote = preview.Pages.Count < preview.TotalPageCount
                ? Loc.F(StringKeys.Viewer.NotePages, preview.Pages.Count, preview.TotalPageCount)
                : Loc.Plural(StringKeys.Viewer.NotePageCount, preview.TotalPageCount);
            SetStatus(StringKeys.Status.ViewerShowing, item.Name);
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed; whoever did that already owns the panel's state.
        }
        catch (InvalidOperationException ex)
        {
            ViewerNote = Loc.T(StringKeys.Status.ViewerOpenFailed);
            SetFailure(FormatDriveError(item.Path, ex), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewerNote = Loc.T(StringKeys.Status.ViewerReadFailed);
            SetStatus(StringKeys.Status.ViewerError, item.Name, ex.DescribeForUser().Render());
            IsWarning = true;
        }
        finally
        {
            EndPreview(cts);
        }
    }

    /// <summary>
    /// Shared setup for both preview flows: supersede any in-flight download and reset the panel to
    /// a clean loading state for <paramref name="item"/>, clearing whichever content type the
    /// previous preview left behind.
    /// </summary>
    private CancellationTokenSource BeginPreview(DriveItem item)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        IsViewerVisible = true;
        IsViewerLoading = true;
        ViewerTitle = item.Name;
        ViewerPath = item.Path;
        ViewerText = string.Empty;
        ViewerImageBytes = null;
        ViewerPdfPages = null;
        ViewerNote = Loc.T(StringKeys.Viewer.NoteDownloading);
        SetStatus(StringKeys.Status.ViewerOpening, item.Name);
        return cts;
    }

    private void EndPreview(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_previewCts, cts))
        {
            IsViewerLoading = false;
            _previewCts = null;
            cts.Dispose();
        }
    }

    private static string FormatViewerNote(TextFilePreview preview)
    {
        // "más de" when the read stopped at the byte limit: ByteCount is what was read, not the
        // file's size, and printing it as the size would be a lie of exactly one byte.
        var localizer = Localizer.Instance;
        var size = preview.ByteCount > TextPreviewPolicy.MaxPreviewBytes
            ? localizer.F(StringKeys.Viewer.NoteMoreThan, TextPreviewPolicy.MaxPreviewBytes.ToString("n0", localizer.Culture))
            : localizer.F(StringKeys.Viewer.NoteBytes, preview.ByteCount.ToString("n0", localizer.Culture));
        var note = localizer.F(StringKeys.Viewer.NoteText, preview.LineCount.ToString("n0", localizer.Culture), size, preview.EncodingName);
        return preview.IsTruncated
            ? note + localizer.T(StringKeys.Viewer.NoteTruncated)
            : note;
    }

    private bool CanViewSelectedFile()
        => _selectedNode is { CanPreview: true } && (_previewLoader is not null || _imagePreviewLoader is not null || _pdfPreviewLoader is not null);

    private async Task ViewSelectedFileAsync()
    {
        if (_selectedNode is not { } node)
        {
            SetStatus(StringKeys.Status.ViewerSelectFile);
            IsWarning = true;
            return;
        }

        await PreviewItemAsync(node.Item);
    }

    private async Task CloseViewerAsync()
    {
        CommitViewerZoom();

        _previewCts?.Cancel();
        IsViewerVisible = false;
        IsViewerLoading = false;
        ViewerImageBytes = null;
        ViewerPdfPages = null;
        ViewerText = string.Empty;
        ViewerNote = string.Empty;
        await Task.CompletedTask;
    }

    public async Task RenameItemAsync(DriveItem item)
    {
        var requester = RequestRenameAsync;
        if (requester is null)
        {
            SetStatus(StringKeys.Status.RenameUnavailable);
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
            SetStatus(StringKeys.Status.RenameProgress, item.Name, newName);
            await _provider.Operations.RenameItemAsync(item.Path, newName);
            SetStatus(StringKeys.Status.RenameDone, item.Name, newName);

            // Update DB immediately
            var parentPath = GetParentPath(item.Path);
            var newPath = _provider.Paths.Combine(parentPath, newName);
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
            SetFailure(FormatDriveError(item.Path, ex), ex);
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
            SetStatus(StringKeys.Status.CopyUnavailable);
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
            SetStatus(StringKeys.Status.CopyProgress, item.Name, displayTarget, CurrentPath);
            await _provider.Operations.CopyItemAsync(item.Path, CurrentPath, string.IsNullOrEmpty(newName) ? null : newName);
            SetStatus(StringKeys.Status.CopyDone, item.Name);
            await InvalidateDeepMetricsAsync(CurrentPath);
            
            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(item.Path, ex), ex);
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
            var confirm = RequestConfirmationAsync;
            if (confirm is not null && !await confirm(
                Loc.F(StringKeys.Status.TrashConfirmFolder, item.Name)))
            {
                SetStatus(StringKeys.Status.TrashCancelledOne, item.Name);
                return;
            }
        }

        try
        {
            IsLoading = true;
            SetStatus(StringKeys.Status.TrashProgress, item.Name);
            await _provider.Operations.TrashItemAsync(item.Path);
            SetStatus(StringKeys.Status.TrashDoneOne, item.Name);

            // Update DB immediately
            await _cacheService.RemoveItemAsync(item.Path);
            await InvalidateDeepMetricsAsync(item.Path);

            _ = RefreshAsync(); // Refresh in background
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(item.Path, ex), ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"Copiar ruta" (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6) — puts the cloud item's path on the system clipboard.</summary>
    public async Task CopyPathAsync(DriveItem item)
    {
        var copy = RequestCopyToClipboardAsync;
        if (copy is null)
        {
            SetStatus(StringKeys.Status.CopyUnavailable);
            return;
        }

        await copy(item.Path);
        SetStatus(StringKeys.Status.ClipboardPath, item.Path);
    }

    /// <summary>
    /// "Share Link" — only reachable when <c>_provider.Capabilities.SupportsShareLinks</c> is true
    /// (the row's own <see cref="DriveNodeViewModel.CanShareLink"/> disables the menu entry
    /// otherwise), so the <see cref="DriveException"/> path below is a defensive fallback, not the
    /// normal way this reports "unsupported".
    /// </summary>
    public async Task CreateShareLinkAsync(DriveItem item)
    {
        if (!_provider.Capabilities.SupportsShareLinks)
        {
            SetStatus(StringKeys.Status.ShareUnsupported, _provider.DisplayName);
            IsWarning = true;
            return;
        }

        try
        {
            IsLoading = true;
            SetStatus(StringKeys.Status.ShareProgress, item.Name);
            var url = await _provider.Operations.CreateShareLinkAsync(item.Path);

            var copy = RequestCopyToClipboardAsync;
            if (copy is not null)
            {
                await copy(url);
                SetStatus(StringKeys.Status.ShareCopied, url);
            }
            else
            {
                SetStatus(StringKeys.Status.ShareLink, url);
            }
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(item.Path, ex), ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// "Subir a esta carpeta..." on a cloud folder row — the same upload path as the toolbar's
    /// <see cref="UploadAsync"/>, but targeting the right-clicked folder instead of always
    /// <see cref="CurrentPath"/>.
    /// </summary>
    public async Task UploadToFolderAsync(DriveItem folder)
    {
        if (!folder.IsFolder)
        {
            return;
        }

        var picker = RequestUploadFilesAsync;
        if (picker is null)
        {
            SetStatus(StringKeys.Status.UploadUnavailable);
            return;
        }

        var files = await picker();
        if (files.Count == 0)
        {
            return;
        }

        var strategy = await ResolveUploadConflictStrategyAsync(files, folder.Path);
        if (strategy is null)
        {
            SetStatus(StringKeys.Status.UploadCancelled);
            return;
        }

        try
        {
            IsLoading = true;
            SetStatusPlural(StringKeys.Status.UploadProgress, files.Count, folder.Path);
            await _provider.Operations.UploadFilesAsync(files, folder.Path, strategy.Value);
            SetStatusPlural(StringKeys.Status.UploadDone, files.Count, folder.Path);
            await InvalidateDeepMetricsAsync(folder.Path);

            if (string.Equals(folder.Path, CurrentPath, StringComparison.Ordinal))
            {
                _ = RefreshAsync(); // Refresh in background
            }
        }
        catch (InvalidOperationException ex)
        {
            SetFailure(FormatDriveError(folder.Path, ex), ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"Descargar aquí" — a quick download into whatever folder the local pane currently shows, reusing the drag-and-drop download path (<see cref="HandleCloudItemsDroppedAsync"/>).</summary>
    public Task DownloadToLocalPaneAsync(DriveItem item)
        => HandleCloudItemsDroppedAsync(new[] { item }, LocalExplorer.CurrentPath);

    /// <summary>"Sincronizar esta ruta..." on a cloud folder row — opens the wizard pre-filled with this folder as the remote side.</summary>
    public Task SyncSelectedRemotePathAsync(DriveItem item)
        => item.IsFolder ? SyncPanel.AddPairAsync(new SyncPairPrefill(item.Path, null)) : Task.CompletedTask;

    /// <summary>
    /// The local path a remote path maps to, or null when it is not inside any sync pair. Routed
    /// through <see cref="PathMapper"/> rather than composed here: that class is the single place
    /// allowed to convert between the three path shapes (docs/PLAN-LOCAL-SYNC.md §3.2's golden
    /// rule), and its <c>ToLocalAbsolute</c> is what already produces the paths the sync engine
    /// actually writes to — so the dialog cannot disagree with what sync does.
    /// </summary>
    private string? LocalPathFor(string remotePath)
    {
        var pair = SyncPanel.FindPairContainingRemotePath(remotePath);
        if (pair is null)
        {
            return null;
        }

        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        return mapper.ToLocalAbsolute(mapper.ToRelativeFromRemote(remotePath));
    }

    /// <summary>The mirror of <see cref="LocalPathFor"/>, for the local pane's own properties dialog.</summary>
    private string? RemotePathFor(string localPath)
    {
        var pair = SyncPanel.FindPairContainingLocalPath(localPath);
        if (pair is null)
        {
            return null;
        }

        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        return mapper.ToRemoteAbsolute(mapper.ToRelativeFromLocal(localPath));
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
            new(Loc.T(StringKeys.Common.Name), item.Name),
            new(Loc.T(StringKeys.Common.Path), item.Path, IsCopyable: true),
            new(Loc.T(StringKeys.Common.Type), Loc.T(item.IsFolder ? StringKeys.Common.Folder : StringKeys.Common.File)),
        };

        // Where this lives on the machine, when it is inside a sync pair. The pair already knows;
        // the dialog just never asked (docs/PLAN-UX-ROUND-2.md §12).
        if (LocalPathFor(item.Path) is { } localPath)
        {
            fields.Add(new PropertyField(Loc.T(StringKeys.Explorer.LocalPath), localPath, IsCopyable: true));
        }

        if (item.Size is not null)
        {
            fields.Add(new PropertyField(Loc.T(StringKeys.Common.Size), Loc.F(StringKeys.Common.Bytes, item.Size.Value.ToString("n0", Loc.Culture))));
        }

        if (item.ModifiedAt is not null)
        {
            fields.Add(new PropertyField(Loc.T(StringKeys.Common.Modified), item.ModifiedAt.Value.ToLocalTime().ToString("g", Loc.Culture)));
        }

        await show(item.Name, fields);
    }

    /// <summary>
    /// A plain click. Selection only, in every view mode (docs/PLAN-UX-ROUND-3.md X2) — opening
    /// moved to the double click. Before this, a single click both selected and opened, which is
    /// why the tile modes could not select at all: their only gesture already meant "navigate".
    /// </summary>
    private async Task SelectRowAsync(DriveItem item)
    {
        SelectRow(RootItems.FirstOrDefault(node => node.Item.Path == item.Path));
        SelectItem(item);
        await Task.CompletedTask;
    }

    /// <summary>
    /// A double click, Enter, or the context menu's "Open": into the folder, or the preview for a
    /// file that has one. Selects first, so activating a row a keyboard moved to also updates the
    /// details panel.
    /// </summary>
    private async Task HandleRowClickAsync(DriveItem item)
    {
        SelectRow(RootItems.FirstOrDefault(node => node.Item.Path == item.Path));
        SelectItem(item);

        if (item.IsFolder)
        {
            await NavigateIntoAsync(item.Path);
            return;
        }

        // Opening a file opens it (docs/PLAN-UX-ROUND-4.md Y1). This returned here, which was
        // invisible while a click both selected and opened — X2 made the double click the open
        // gesture, and then double-clicking a file did nothing at all while the plan and the commit
        // message both said it previewed. A file with no preview still just selects: there is
        // nothing else this app can do with it.
        if (PreviewPolicy.CanPreview(item))
        {
            await PreviewItemAsync(item);
        }
    }

    /// <summary>
    /// A plain click (no modifier): selects just <paramref name="node"/>, clearing every other
    /// row's selection — a plain click always resets a file manager's selection to "just this one",
    /// even if several rows were multi-selected beforehand.
    /// </summary>
    private void SelectRow(DriveNodeViewModel? node)
    {
        foreach (var selected in RootItems.Where(n => n.IsSelected).ToList())
        {
            selected.IsSelected = false;
        }

        _selectedNode = node;
        _selectionAnchorPath = node?.Path;

        if (node is not null)
        {
            node.IsSelected = true;
        }

        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        RaiseSelectionSummaryChanged();
    }

    /// <summary>Ctrl/Cmd+Click: adds or removes just this row, leaving every other row's selection untouched (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2).</summary>
    public void ToggleSelection(DriveNodeViewModel node)
    {
        node.IsSelected = !node.IsSelected;
        _selectionAnchorPath = node.Path;
        _selectedNode = SelectedCount == 1 ? RootItems.FirstOrDefault(n => n.IsSelected) : null;
        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        RaiseSelectionSummaryChanged();
    }

    /// <summary>Shift+Click: selects the contiguous run between the last-touched row (the anchor) and this one, replacing whatever was selected before — standard file-manager range-select.</summary>
    public void SelectRange(DriveNodeViewModel target)
    {
        var anchorIndex = _selectionAnchorPath is null ? -1 : RootItems.ToList().FindIndex(n => n.Path == _selectionAnchorPath);
        var targetIndex = RootItems.IndexOf(target);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            SelectRow(target);
            return;
        }

        var (lo, hi) = anchorIndex <= targetIndex ? (anchorIndex, targetIndex) : (targetIndex, anchorIndex);
        for (var i = 0; i < RootItems.Count; i++)
        {
            RootItems[i].IsSelected = i >= lo && i <= hi;
        }

        _selectedNode = SelectedCount == 1 ? RootItems.FirstOrDefault(n => n.IsSelected) : null;
        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        RaiseSelectionSummaryChanged();
    }

    private Task SelectAllRowsAsync()
    {
        foreach (var node in RootItems)
        {
            node.IsSelected = true;
        }

        _selectionAnchorPath = RootItems.Count > 0 ? RootItems[0].Path : null;
        _selectedNode = SelectedCount == 1 ? RootItems.FirstOrDefault(n => n.IsSelected) : null;
        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        RaiseSelectionSummaryChanged();
        return Task.CompletedTask;
    }

    private void RaiseSelectionSummaryChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(IsSingleSelected));
        OnPropertyChanged(nameof(SelectionSummaryText));
        DownloadSelectedCommand.RaiseCanExecuteChanged();
        TrashSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The batch counterpart to <see cref="DownloadItemAsync"/>: every selected file (folders skipped, same rule the single-row command already follows) into one picked destination.</summary>
    private async Task DownloadSelectedAsync()
    {
        var files = RootItems.Where(n => n.IsSelected && n.IsFile).Select(n => n.Item).ToList();
        if (files.Count == 0)
        {
            SetStatus(StringKeys.Status.DownloadSelectFiles);
            IsWarning = true;
            return;
        }

        var picker = RequestDownloadFolderAsync;
        if (picker is null)
        {
            SetStatus(StringKeys.Status.DownloadUnavailable);
            return;
        }

        var folder = await picker();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var failed = new List<string>();
        try
        {
            IsLoading = true;
            foreach (var file in files)
            {
                try
                {
                    SetStatus(StringKeys.Status.DownloadProgress, file.Name);
                    await _provider.Operations.DownloadFileAsync(file.Path, folder);
                }
                catch (InvalidOperationException ex)
                {
                    failed.Add(Loc.F(StringKeys.Status.DownloadItemError, file.Name, FormatDriveError(file.Path, ex).Render()));
                }
            }
        }
        finally
        {
            IsLoading = false;
        }

        SetStatus(failed.Count == 0
            ? LocalizedText.Plural(StringKeys.Status.DownloadBatchDone, files.Count, folder)
            : LocalizedText.Of(StringKeys.Status.DownloadBatchPartial, files.Count - failed.Count, files.Count, string.Join("; ", failed)));
        IsWarning = failed.Count > 0;
    }

    /// <summary>The batch counterpart to <see cref="TrashItemAsync"/>: one confirmation for the whole selection (only asked when it includes a folder, same as the single-row command), then each item independently so one failure doesn't abandon the rest.</summary>
    private async Task TrashSelectedAsync()
    {
        var selected = RootItems.Where(n => n.IsSelected).Select(n => n.Item).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Any(item => item.IsFolder))
        {
            var confirm = RequestConfirmationAsync;
            if (confirm is not null && !await confirm(Loc.Plural(StringKeys.Status.TrashConfirmMany, selected.Count)))
            {
                SetStatusPlural(StringKeys.Status.TrashCancelledMany, selected.Count);
                return;
            }
        }

        var failed = new List<string>();
        try
        {
            IsLoading = true;
            foreach (var item in selected)
            {
                try
                {
                    SetStatus(StringKeys.Status.TrashProgress, item.Name);
                    await _provider.Operations.TrashItemAsync(item.Path);
                    await _cacheService.RemoveItemAsync(item.Path);
                    await InvalidateDeepMetricsAsync(item.Path);
                }
                catch (InvalidOperationException ex)
                {
                    failed.Add(Loc.F(StringKeys.Status.DownloadItemError, item.Name, FormatDriveError(item.Path, ex).Render()));
                }
            }
        }
        finally
        {
            IsLoading = false;
        }

        SetStatus(failed.Count == 0
            ? LocalizedText.Plural(StringKeys.Status.TrashDoneMany, selected.Count)
            : LocalizedText.Of(StringKeys.Status.TrashPartial, selected.Count - failed.Count, selected.Count, string.Join("; ", failed)));
        IsWarning = failed.Count > 0;

        _ = RefreshAsync();
    }

    private async Task LoadFolderAsync(string path, bool clearSelection, bool forceFreshRemoteView = false)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            IsLoading = true;
            SetStatus(StringKeys.Status.LoadProgress, path);

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
                SetStatus(StringKeys.Status.LoadCached, path);
                IsLoading = false;

                // Fire and forget CLI fetch to keep UI responsive and command finished. (Tried
                // making this await when forceFreshRemoteView is set, so a post-transfer refresh
                // could guarantee the listing was current before returning — reverted: this method
                // posts its own UI update through Dispatcher.UIThread.InvokeAsync, which never
                // completes without a running Avalonia dispatcher, so awaiting it deadlocked every
                // test that exercises a forced refresh instead of just running slower.)
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

                    SetStatusPlural(StringKeys.Status.LoadDone, RootItems.Count, path);
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
                SetStatus(StringKeys.Status.LoadCacheFailed, path, ex.Message);
                IsWarning = true;
            });
        }
    }

    private void HandleLoadError(string path, InvalidOperationException ex)
    {
        var kind = (ex as DriveException)?.Kind ?? DriveErrorKind.Unknown;
        _lastErrorKind = kind;

        if (kind == DriveErrorKind.NotFound)
        {
            SetStatus(StringKeys.Status.LoadGone, path);
            IsWarning = true;
            return;
        }

        if (kind == DriveErrorKind.NotAuthenticated)
        {
            IsAuthenticated = false;
        }

        SetFailure(FormatDriveError(path, ex), ex);
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

        // Same reasoning, unconditionally: a search term almost never matches anything in a
        // different folder, and even when it does, silently carrying it over would look like the
        // listing forgot files rather than like an active filter.
        if (_searchText.Length > 0)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
        }

        RenderItems();
        UpdateQuotaMetrics();
        UpdateConnectionTelemetry();
    }

    private void RenderItems()
    {
        // Rebuilding replaces every row's view-model, so the previous selection highlight would
        // otherwise vanish even on a plain refresh (which intentionally keeps the side panel's
        // selection) — carry it forward onto whichever new rows still match those paths, single or
        // multi alike.
        var previouslySelectedPaths = RootItems.Where(n => n.IsSelected).Select(n => n.Path).ToHashSet();
        _selectedNode = null;

        var visible = _kindFilter is null
            ? _loadedItems
            : _loadedItems.Where(item => FileKindClassifier.Classify(item.Name, item.IsFolder) == _kindFilter).ToList();

        if (_searchText.Length > 0)
        {
            visible = visible.Where(item => item.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        RootItems.Clear();
        foreach (var item in DriveItemSorter.Sort(visible, SortKey, SortDescending))
        {
            var node = new DriveNodeViewModel(item, HandleRowClickAsync, SelectRowAsync, DownloadItemAsync, TrashItemAsync, RenameItemAsync, CopyItemAsync, PreviewItemAsync, HandleUnexpectedError, new DriveNodeSyncActions
            {
                FindSyncPair = i => SyncPanel.FindPairByRemotePath(i.Path),
                SyncSelectedPathAsync = SyncSelectedRemotePathAsync,
                CopyPathAsync = CopyPathAsync,
                UploadToFolderAsync = UploadToFolderAsync,
                DownloadHereAsync = DownloadToLocalPaneAsync,
                ShowPropertiesAsync = ShowPropertiesAsync,
                SupportsShareLinks = _provider.Capabilities.SupportsShareLinks,
                CreateShareLinkAsync = CreateShareLinkAsync,
                RefreshPaneAsync = RefreshAsync,
            });
            if (previouslySelectedPaths.Contains(item.Path))
            {
                node.IsSelected = true;
            }

            RootItems.Add(node);
        }

        _selectedNode = SelectedCount == 1 ? RootItems.FirstOrDefault(n => n.IsSelected) : null;
        _selectionAnchorPath = _selectedNode?.Path ?? _selectionAnchorPath;
        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        SelectAllRowsCommand.RaiseCanExecuteChanged();
        RaiseSelectionSummaryChanged();
        OnPropertyChanged(nameof(SearchResultText));
        // Only after a listing has actually been rendered once: before that, "no rows" means "no
        // load has happened yet", and the empty state would flash on the way to the first paint.
        _hasRenderedListing = true;
        RaiseEmptyStateChanged();

        // Computed here rather than at each call site so the cached paint and the CLI result both
        // update it, and so the numbers can never disagree with the rows actually on screen.
        // Built from everything loaded, never from the filtered rows: metrics answer "what is in
        // this folder", and a total that silently followed the filter would be a different question
        // wearing the same label.
        var metrics = FolderMetricsCalculator.FromChildren(CurrentPath, _loadedItems, _timeProvider.GetUtcNow());
        Metrics.Update(metrics);
        RebuildKindFilters(metrics);

        // Covers both filters together rather than each setting its own summary: a search term and
        // a kind chip can be active at once, and the count on screen is the result of whichever of
        // them are, not just the last one applied.
        FilterSummary = _kindFilter is not null || _searchText.Length > 0
            ? Loc.F(StringKeys.Explorer.FilterSummary, RootItems.Count.ToString("n0", Loc.Culture), _loadedItems.Count.ToString("n0", Loc.Culture))
            : string.Empty;

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

        // The root always gets its own leading, always-clickable segment. Proton's root is a
        // real named folder ("/my-files"), so splitting the path on '/' always produced at
        // least one segment to click back to. OneDrive's root is bare "/", which splits into
        // *zero* segments — so browsing into a folder left the breadcrumb bar showing only that
        // folder's name, with nothing before it to get back to root: it looked like the folder
        // itself was the root. Labeling this segment with the provider's name when the root has
        // no real name of its own (OneDrive) keeps Proton's own label ("my-files") unchanged.
        var rootLabel = _rootPath == "/" ? _provider.DisplayName : _rootPath.TrimEnd('/').Split('/').Last();
        BreadcrumbItems.Add(new BreadcrumbSegmentViewModel(rootLabel, _rootPath, path == _rootPath, NavigateIntoAsync, HandleUnexpectedError));

        if (path == _rootPath)
        {
            return;
        }

        var relative = path[_rootPath.Length..].Trim('/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = _rootPath;

        foreach (var segment in segments)
        {
            currentPath = currentPath.TrimEnd('/') + "/" + segment;
            BreadcrumbItems.Add(new BreadcrumbSegmentViewModel(segment, currentPath, currentPath == path, NavigateIntoAsync, HandleUnexpectedError));
        }
    }

    private void SelectItem(DriveItem item)
    {
        _selectedItem = item;
        RefreshSelectionLabels();
        HasSelection = true;
        SetStatus(StringKeys.Status.Selected, item.Name);
    }

    /// <summary>
    /// Derives the details sidebar's seven labels from the selected item. Split out of
    /// <see cref="SelectItem"/> so a language change can re-derive them without also re-announcing
    /// the selection in the status line.
    /// </summary>
    private void RefreshSelectionLabels()
    {
        var none = Loc.T(StringKeys.Common.None);
        if (_selectedItem is not { } item)
        {
            SelectedName = none;
            SelectedKind = none;
            SelectedPath = none;
            SelectedSize = none;
            SelectedModified = none;
            SelectedOwner = none;
            SelectedShared = none;
            return;
        }

        SelectedName = item.Name;
        SelectedKind = Loc.T(item.IsFolder ? StringKeys.Common.Folder : StringKeys.Common.File);
        SelectedPath = item.Path;
        SelectedSize = item.Size is { } size ? Loc.F(StringKeys.Common.Bytes, size.ToString("n0", Loc.Culture)) : none;
        SelectedModified = item.ModifiedAt is { } modifiedAt ? modifiedAt.ToLocalTime().ToString("g", Loc.Culture) : none;
        SelectedOwner = item.Owner ?? none;
        SelectedShared = Loc.T(item.IsShared ? StringKeys.Common.Yes : StringKeys.Common.No);
    }

    private void ClearSelection()
    {
        foreach (var selected in RootItems.Where(n => n.IsSelected).ToList())
        {
            selected.IsSelected = false;
        }

        _selectedNode = null;
        _selectionAnchorPath = null;
        _selectedItem = null;
        RefreshSelectionLabels();
        HasSelection = false;
        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        RaiseSelectionSummaryChanged();
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
            settings.SetProviderAuthenticated(_provider.Id, IsAuthenticated);

            settings.ViewMode = ViewMode.ToString();
            settings.SortKey = SortKey.ToString();
            settings.SortDescending = SortDescending;
            settings.Theme = _theme;
            settings.Language = Loc.Current.Code;
            settings.BandwidthLimitKbps = _bandwidthLimitKbps;
            settings.DefaultSyncFolder = _defaultSyncFolder;
        });
    }

    public void UpdateConnectionTelemetry()
    {
        if (!IsAuthenticated)
        {
            _connectionStatus = Loc.T(StringKeys.Connection.StateDisconnected);
            _connectionStatusKind = "Disconnected";
            _connectionStatusDescription = Loc.F(StringKeys.Connection.DescDisconnected, _provider.DisplayName);
        }
        else if (_status.IsWarning && _lastErrorKind == DriveErrorKind.RateLimited)
        {
            _connectionStatus = Loc.T(StringKeys.Connection.StateRateLimited);
            _connectionStatusKind = "RateLimited";
            _connectionStatusDescription = Loc.F(StringKeys.Connection.DescRateLimited, _provider.DisplayName);
        }
        else if (_status.IsWarning && IsConnectionFailure(_lastErrorKind))
        {
            // Deliberately ahead of the Syncing branch: a sync running on top of a broken
            // connection is not the headline, the broken connection is.
            _connectionStatus = Loc.T(StringKeys.Connection.StateDegraded);
            _connectionStatusKind = "Degraded";
            _connectionStatusDescription = Loc.F(StringKeys.Connection.DescDegraded, _provider.DisplayName, _status.Message);
        }
        else if ((_isSyncInProgress is not null && _isSyncInProgress()) || IsLoading || IsDeepScanRunning)
        {
            _connectionStatus = Loc.T(StringKeys.Connection.StateSyncing);
            _connectionStatusKind = "Syncing";
            _connectionStatusDescription = IsDeepScanRunning
                ? Loc.F(StringKeys.Connection.DescScanning, _currentPath)
                : IsLoading
                    ? Loc.F(StringKeys.Connection.DescLoading, CurrentPath)
                    : Loc.T(StringKeys.Connection.DescSyncing);
        }
        else
        {
            _connectionStatus = Loc.T(StringKeys.Connection.StateOnline);
            _connectionStatusKind = "Online";
            _connectionStatusDescription = Loc.F(StringKeys.Connection.DescConnected, _provider.DisplayName);
        }

        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(ConnectionStatusKind));
        OnPropertyChanged(nameof(ConnectionStatusDescription));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsSyncing));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsRateLimited));
        OnPropertyChanged(nameof(IsDegraded));
        OnPropertyChanged(nameof(IsConnectionActionable));
        OnPropertyChanged(nameof(HasStatusAction));
        OnPropertyChanged(nameof(StatusActionLabel));
        OnPropertyChanged(nameof(StatusActionCommand));
    }

    /// <summary>
    /// Error kinds that say something about the connection rather than about the request. A
    /// <see cref="DriveErrorKind.NotFound"/> on one path means the path is gone; a
    /// <see cref="DriveErrorKind.Network"/> means nothing else will work either, and the header
    /// should stop saying "En línea".
    /// </summary>
    private static bool IsConnectionFailure(DriveErrorKind kind) => kind is
        DriveErrorKind.Network
        or DriveErrorKind.Timeout
        or DriveErrorKind.NotAuthenticated
        or DriveErrorKind.PermissionDenied
        or DriveErrorKind.Busy;

    public void UpdateQuotaMetrics()
    {

        // Only the root listing stands in for "account usage" here — there's no real quota API on
        // the provider seam yet, so this is an approximation. Recomputing it from whatever subfolder
        // is currently browsed would make the gauge jump to near-zero on every navigation.
        if (_currentPath == _rootPath)
        {
            var files = _loadedItems.Where(i => !i.IsFolder).ToList();
            var sized = files.Where(i => i.Size.HasValue).ToList();

            _quotaUsedBytes = sized.Sum(i => i.Size!.Value);

            // "No files at all" is a real zero. "Files, none of which reported a size" is not —
            // that's a provider that doesn't populate the field (Google-native Docs have no size
            // whatsoever, PLAN-CLOUD-PROVIDERS.md §8.4) and summing it yields a confident 0 B.
            _quotaUsedIsKnown = files.Count == 0 || sized.Count > 0;

            // Almost always true, and honestly so: the sum covers the root's own files only, so
            // any subfolder at all makes it a lower bound rather than a total.
            _quotaUsedIsPartial = sized.Count < files.Count || _loadedItems.Any(i => i.IsFolder);
        }

        OnPropertyChanged(nameof(QuotaUsedBytes));
        OnPropertyChanged(nameof(QuotaDisplay));
        OnPropertyChanged(nameof(QuotaTooltip));
        OnPropertyChanged(nameof(IsQuotaUsageKnown));
    }

    public async Task SetThemeAsync(string theme)
    {
        ThemePreference = theme;
        await Task.CompletedTask;
    }

    public async Task CycleThemeAsync()
    {
        var nextTheme = _theme switch
        {
            "Default" => "Light",
            "Light" => "Dark",
            "Dark" => "Default",
            _ => "Default"
        };
        await SetThemeAsync(nextTheme);
    }

    public async Task ToggleSettingsAsync()
    {
        if (IsSettingsView)
        {
            await ShowExplorerAsync();
        }
        else
        {
            await ShowSettingsAsync();
        }
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
        CliUpdate.RaiseCommandStates();
        ViewSelectedFileCommand.RaiseCanExecuteChanged();
        SwitchToProtonCommand.RaiseCanExecuteChanged();
        SwitchToOneDriveCommand.RaiseCanExecuteChanged();
        SwitchToGoogleDriveCommand.RaiseCanExecuteChanged();
        SwitchToNextcloudCommand.RaiseCanExecuteChanged();
        SwitchToS3Command.RaiseCanExecuteChanged();
    }

    private async Task ToggleCommandConsoleAsync()
    {
        IsCommandConsoleVisible = !IsCommandConsoleVisible;
        await Task.CompletedTask;
    }

    private async Task ToggleLocalExplorerPanelAsync()
    {
        IsLocalExplorerPanelVisible = !IsLocalExplorerPanelVisible;
        await Task.CompletedTask;
    }

    private async Task ToggleLogFilterAsync()
    {
        ShowOnlyWarningsAndErrors = !ShowOnlyWarningsAndErrors;
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

    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Both filters at once, from the empty state itself (docs/PLAN-UX-ROUND-3.md X3). The kind
    /// chip clears by clicking the active chip, which is only discoverable to someone who already
    /// knows it — and someone staring at an empty pane does not.
    /// </summary>
    private async Task ClearFiltersAsync()
    {
        _kindFilter = null;
        // Straight through the field: SearchText's setter renders, and rendering twice for one
        // gesture would rebuild the whole listing for nothing.
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        ClearSearchCommand.RaiseCanExecuteChanged();
        RenderItems();
        await Task.CompletedTask;
    }

    private async Task ShowExplorerAsync()
    {
        ActiveView = MainView.Explorer;
        await Task.CompletedTask;
    }

    /// <summary>Switches to the sync pair list, now a top-level view (docs/PLAN-UX-ROUND-2.md §5).</summary>
    private async Task ShowSyncAsync()
    {
        ActiveView = MainView.Sync;
        await Task.CompletedTask;
    }

    private async Task ShowSettingsAsync()
    {
        ActiveView = MainView.Settings;

        // Read it on the way in, so the settings view is never showing a stale or empty version,
        // but only once per configured path — the CLI costs a whole process launch (~3.5s cold).
        await CliUpdate.EnsureVersionReadAsync();
    }

    /// <summary>
    /// Compares the installed CLI against Proton's published Stable release. This is the app's only
    /// outbound network call; everything else goes through the CLI process.
    /// </summary>
    private async Task DownloadActivityAsync()
    {
        var picker = RequestSaveActivityAsync;
        if (picker is null)
        {
            SetStatus(StringKeys.Status.ActivityUnavailable);
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
            SetStatus(StringKeys.Status.ActivitySaved, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(StringKeys.Status.ActivitySaveFailed, path, ex.Message);
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
        CommandLogText = Loc.T(StringKeys.Console.NoCommandRunning);
        ActiveCommand = Loc.T(StringKeys.Console.Idle);
        LastLogLine = null;
        RaiseCommandStates();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Lets the console show activity from a provider session other than the one this view model
    /// browses — the composition root calls this once per additional active session (P7: Proton
    /// and OneDrive can both be configured and syncing at once, even though only one is on screen
    /// at a time until Phase B's account switcher). Lines from every session share one buffer,
    /// prefixed with <paramref name="accountLabel"/> so an interleaved console stays readable.
    /// </summary>
    public void ObserveAdditionalProviderActivity(string accountLabel, ICloudDriveProvider provider)
    {
        provider.Activity += (_, activity) => OnActivity(accountLabel, activity);
        provider.ListingParseWarning += (_, message) => OnListingParseWarning(accountLabel, message);
    }

    private void OnActivity(string accountLabel, ProviderActivity activity)
    {
        switch (activity.Kind)
        {
            case ActivityKind.Started:
                Dispatcher.UIThread.Post(() => ActiveCommand = $"[{accountLabel}] {activity.Label}");
                Dispatcher.UIThread.Post(() => ActiveOperationCount++);
                QueueCommandLine($"[{accountLabel}] > {activity.Label}");
                break;

            case ActivityKind.Output:
                QueueCommandLine($"[{accountLabel}] " + (activity.IsError ? $"[err] {activity.Text}" : activity.Text ?? string.Empty));
                break;

            case ActivityKind.Finished:
                QueueCommandLine($"[{accountLabel}] " + (activity.IsError ? $"[fail] exit {activity.ExitCode}" : $"[done] exit {activity.ExitCode}"));
                // Unconditional, same as before P7: with two sessions active, one session's
                // Finished can clear ActiveCommand out from under the other's still-running
                // Started. A single "what's active" label can't represent two concurrent
                // operations correctly — a real per-session indicator is Phase B's job.
                Dispatcher.UIThread.Post(() => ActiveCommand = Loc.T(StringKeys.Console.Idle));
                // Clamped rather than trusting Started/Finished to always balance: a session added
                // mid-flight (AddBrowsableAccount) only starts observing from that point on, so its
                // first-ever event could be a Finished with no matching Started counted yet.
                Dispatcher.UIThread.Post(() => ActiveOperationCount = Math.Max(0, ActiveOperationCount - 1));
                break;
        }
    }

    private void OnListingParseWarning(string accountLabel, string message)
        => QueueCommandLine($"[{accountLabel}] [warn] {message}");

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
        LastLogLine = batch[^1];
        RefreshCommandLogText();

        // Only the two activity commands depend on the line count, and only on the empty/non-empty
        // transition. Re-raising all thirteen on every line was pure waste on the UI thread.
        if (countBefore == 0 && _commandLog.Count > 0)
        {
            DownloadActivityCommand.RaiseCanExecuteChanged();
            ClearActivityCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Re-renders <see cref="CommandLogText"/> from the buffer plus whatever the warnings-only
    /// filter and search box currently ask for — called both when new lines arrive and when either
    /// filter input changes, so the two stay in sync without keeping a second copy of the text.
    /// </summary>
    private void RefreshCommandLogText()
    {
        IEnumerable<string> lines = _commandLog.Lines;

        if (_showOnlyWarningsAndErrors)
        {
            lines = lines.Where(line => line.Contains("[warn]", StringComparison.Ordinal)
                || line.Contains("[err]", StringComparison.Ordinal)
                || line.Contains("[fail]", StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(_logSearchText))
        {
            lines = lines.Where(line => line.Contains(_logSearchText, StringComparison.OrdinalIgnoreCase));
        }

        CommandLogText = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Adds lines straight to the buffer and re-renders, bypassing QueueCommandLine/FlushCommandLog's
    /// <c>Dispatcher.UIThread.Post</c>. Internal rather than private for the same reason as
    /// <see cref="DisplayItems"/>: that Post never completes without a running Avalonia dispatcher,
    /// so a test that went through the real activity pipeline to get lines into the log would hang.
    /// </summary>
    internal void AppendCommandLogLinesForTests(IEnumerable<string> lines)
    {
        var list = lines as IReadOnlyList<string> ?? lines.ToList();
        _commandLog.AddRange(list);
        if (list.Count > 0)
        {
            LastLogLine = list[^1];
        }

        RefreshCommandLogText();
    }

    /// <summary>
    /// Catch-all for exceptions that escape a command's Func&lt;Task&gt; and are not the
    /// expected InvalidOperationException the CLI layer throws. Without this, AsyncCommand's
    /// async void Execute would let the exception terminate the process.
    /// </summary>
    /// <summary>
    /// The same sink <see cref="AsyncCommand"/> uses, for the view's own <c>async void</c> event
    /// handlers. They are not commands, so nothing routes their exceptions anywhere: an exception
    /// escaping one of them terminates the process (AGENTS.md's own non-negotiable, and
    /// docs/PLAN-UX-ROUND-4.md Z1 for the four that had no guard at all).
    /// </summary>
    internal void ReportHandlerFailure(Exception ex) => HandleUnexpectedError(ex);

    private void HandleUnexpectedError(Exception ex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CrashLog.Write(ex);
            SetStatus(StringKeys.Status.UnexpectedError, ex.Message);
            IsWarning = true;
            QueueCommandLine($"[err] Unexpected error: {ex}");
            IsLoading = false;
        });
    }

    // Records the kind alongside formatting the message so callers like UpdateConnectionTelemetry
    // can switch on the shared DriveErrorKind taxonomy instead of pattern-matching the human-readable
    // StatusMessage text it produces (AGENTS.md: "Errors are typed").
    /// <summary>
    /// The framing sentence around a provider failure. Returns an unrendered
    /// <see cref="LocalizedText"/> so a status line holding one follows the language picker; the
    /// provider's own message goes in verbatim as an argument, untranslated, because that sentence
    /// is the provider's and not ours (docs/PLAN-I18N.md §9).
    /// </summary>
    private LocalizedText FormatDriveError(string path, Exception ex)
    {
        var kind = (ex as DriveException)?.Kind ?? DriveErrorKind.Unknown;
        _lastErrorKind = kind;

        if (kind == DriveErrorKind.NotAuthenticated)
        {
            return path == "auth login"
                ? LocalizedText.Of(StringKeys.Error.NeedAuth)
                : LocalizedText.Of(StringKeys.Error.NeedAuthToLoad, path);
        }

        // Three sources, in order of how much they know. The exception's own translated sentence
        // when it has one (PLAN-TECH-DEBT.md B6.5); otherwise the provider's own words verbatim,
        // because paraphrasing them loses the detail that says whose problem this is; and if there
        // are none, the typed kind still has something to say.
        var described = ex.DescribeForUser();
        var detail = described.IsEmpty ? DriveErrorPresenter.Describe(kind) : described.Render();

        if (path == "auth logout")
        {
            return LocalizedText.Of(StringKeys.Error.LogoutFailed, detail);
        }

        return LocalizedText.Of(StringKeys.Error.LoadFailed, path, detail);
    }

    private static string? PlaceholderIdentity(ProviderId id) => id switch
    {
        ProviderId.Proton => "user@proton.me",
        ProviderId.OneDrive => "user@outlook.com",
        ProviderId.GoogleDrive => "user@gmail.com",
        ProviderId.Nextcloud => "user@nextcloud.local",
        ProviderId.S3 => "s3-bucket-primary",
        _ => null
    };

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
