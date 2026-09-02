using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// Backs the Sync window: the list of configured pairs plus "add a pair" flow. See
/// docs/PLAN-LOCAL-SYNC.md §12. All three directions and all four conflict policies are
/// selectable as of F2.
/// </summary>
public sealed class SyncPanelViewModel : ObservableObject
{
    /// <summary>
    /// One active account's sync machinery, grouped for the multi-account merge (P7 Phase A,
    /// docs/PLAN-CLOUD-PROVIDERS.md). The primary slot (index 0, built by the constructor) is what
    /// every existing single-account property/command still reads — <see cref="AddAccount"/> only
    /// ever appends, so nothing about the primary slot's behavior changes for callers that never
    /// call it (every existing test).
    /// </summary>
    private sealed record AccountSlot(string DisplayName, SyncStateStore StateStore, SyncExecutor Executor, SyncCrashRecovery CrashRecovery, SyncScheduler? Scheduler);

    private readonly List<AccountSlot> _slots = new();
    private string _statusMessage;
    private bool _isBusy;
    private bool _hasRecovered;
    private string? _providerFilter;
    private AccountSlot? _activeSlotOverride;

    private AccountSlot Primary => _slots[0];

    /// <summary>
    /// Which account a newly-added pair targets — <see cref="Primary"/> (the account this panel
    /// was constructed with) until <see cref="SetActiveAccount"/> says otherwise. P7 Phase B
    /// (docs/PLAN-CLOUD-PROVIDERS.md) made which account the browser shows switchable at runtime;
    /// before that, "Primary" and "whichever account the user is currently looking at" were always
    /// the same account, so nothing here needed to track it separately. Falls back to
    /// <see cref="Primary"/> if the requested account isn't (or is no longer) registered, rather
    /// than throwing — a stale override must never crash "Add pair".
    /// </summary>
    private AccountSlot ActiveSlot => _activeSlotOverride ?? Primary;

    /// <summary>
    /// Called by <c>MainWindowViewModel.SwitchBrowserAccountAsync</c> whenever the browsed account
    /// changes, so a pair added afterward is created against the account the user is actually
    /// looking at — not always whichever account was "primary" at startup. A no-op for an unknown
    /// label (falls back to <see cref="Primary"/> via <see cref="ActiveSlot"/>).
    /// </summary>
    public void SetActiveAccount(string displayName)
        => _activeSlotOverride = _slots.FirstOrDefault(slot => slot.DisplayName == displayName);

    /// <param name="providerDisplayName">
    /// Named in a couple of user-facing strings ("Add a folder to start syncing it from…").
    /// Defaults to "Proton Drive" so every existing call site (tests above all) keeps working
    /// unchanged; the composition root passes the active provider's real name
    /// (docs/PLAN-CLOUD-PROVIDERS.md §5 item 3).
    /// </param>
    public SyncPanelViewModel(SyncStateStore stateStore, SyncExecutor executor, SyncCrashRecovery crashRecovery, SyncScheduler? scheduler = null, string providerDisplayName = "Proton Drive")
    {
        _statusMessage = $"Add a folder to start syncing it from {providerDisplayName}.";
        Pairs = new ObservableCollection<SyncPairViewModel>();
        Pairs.CollectionChanged += (_, _) => RebuildProviderFilters();
        AccountSyncToggles = new ObservableCollection<AccountSyncToggleViewModel>();
        ProviderFilters = new ObservableCollection<ProviderFilterViewModel>();

        AddPairCommand = new AsyncCommand(() => AddPairAsync(), () => !IsBusy, ReportError);
        RefreshCommand = new AsyncCommand(LoadPairsAsync, () => !IsBusy, ReportError);
        ToggleAutomaticSyncCommand = new AsyncCommand(ToggleAutomaticSyncAsync, () => Primary.Scheduler is not null, ReportError);

        AddSlot(new AccountSlot(providerDisplayName, stateStore, executor, crashRecovery, scheduler));
    }

    /// <summary>
    /// Registers a second (or later) active account's sync machinery alongside the primary one —
    /// P7 Phase A: Proton and OneDrive can both be configured and syncing at once. Its pairs merge
    /// into the same <see cref="Pairs"/> list (each row already knows which account it belongs to
    /// via <see cref="SyncPairViewModel"/>'s account label), and it gets its own independent
    /// <see cref="AccountSyncToggleViewModel"/> — pausing one account's automatic sync must not
    /// touch another's.
    /// </summary>
    /// <summary>
    /// Registers the slot only — it does <b>not</b> trigger a reload itself. <see cref="LoadPairsAsync"/>
    /// isn't safe to run concurrently with itself (it clears <see cref="Pairs"/> before rebuilding
    /// it), and every real caller adds every account before the first
    /// <see cref="InitializeAsync"/>/<see cref="RecoverFromPreviousRunAsync"/> call anyway (the
    /// composition root calls this synchronously for each account, then
    /// <c>MainWindowViewModel.InitializeAsync</c> loads the panel once, afterward). A caller that
    /// adds an account to an already-initialized panel should follow up with
    /// <see cref="RefreshCommand"/> itself.
    /// </summary>
    public void AddAccount(SyncStateStore stateStore, SyncExecutor executor, SyncCrashRecovery crashRecovery, SyncScheduler? scheduler, string providerDisplayName)
        => AddSlot(new AccountSlot(providerDisplayName, stateStore, executor, crashRecovery, scheduler));

    private void AddSlot(AccountSlot slot)
    {
        _slots.Add(slot);
        AccountSyncToggles.Add(new AccountSyncToggleViewModel(slot.DisplayName, slot.Scheduler, slot.StateStore));

        if (slot.Scheduler is not null)
        {
            // A cycle the scheduler ran on its own still has to be reflected in the row the user
            // is looking at, or the panel would show stale "Up to date" times.
            slot.Scheduler.PairSynced += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadPairsAsync());
            slot.Scheduler.WatcherDegraded += (_, reason) => Dispatcher.UIThread.Post(() => StatusMessage = $"{slot.DisplayName}: {reason}");
        }
    }

    public ObservableCollection<SyncPairViewModel> Pairs { get; }

    /// <summary>One entry per active account (including the primary), for the per-account automatic-sync toggles — see <see cref="AccountSyncToggleViewModel"/>.</summary>
    public ObservableCollection<AccountSyncToggleViewModel> AccountSyncToggles { get; }

    /// <summary>
    /// The Sync window's "filter by account" chips (docs/PLAN-CLOUD-PROVIDERS.md P9) — empty (and
    /// hidden by the view) with a single account, where every pair obviously belongs to it already,
    /// same rule <see cref="SyncPairViewModel.AccountLabel"/> itself already follows.
    /// </summary>
    public ObservableCollection<ProviderFilterViewModel> ProviderFilters { get; }

    /// <summary>Whether the filter row has anything worth showing — false with a single account.</summary>
    public bool HasProviderFilters => ProviderFilters.Count > 0;

    /// <summary>
    /// <see cref="Pairs"/> narrowed to the active <see cref="ProviderFilters"/> chip, or every pair
    /// when none is active. <see cref="Pairs"/> itself stays the unfiltered source of truth every
    /// existing caller (including every test) already reads — this is purely a view of it, the same
    /// relationship <c>MainWindowViewModel.RootItems</c> has to its own unfiltered <c>_loadedItems</c>.
    /// </summary>
    public IEnumerable<SyncPairViewModel> VisiblePairs
        => _providerFilter is null ? Pairs : Pairs.Where(pair => pair.AccountLabel == _providerFilter);

    public AsyncCommand AddPairCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand ToggleAutomaticSyncCommand { get; }

    public bool IsAutomaticSyncRunning => Primary.Scheduler?.IsRunning ?? false;

    /// <summary>
    /// Whether a pair is mid-scan or mid-transfer right now. Distinct from
    /// <see cref="IsAutomaticSyncRunning"/>, which only says the scheduler loop is enabled —
    /// consulted before replacing the `proton-drive` binary, where "a transfer is in flight" and
    /// "automatic sync is switched on" are very different risks.
    /// </summary>
    public bool IsSyncInProgress => IsBusy || Pairs.Any(pair => pair.IsBusy);

    public string AutomaticSyncLabel => IsAutomaticSyncRunning ? "⏸ Automatic sync: on" : "▶ Automatic sync: off";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AddPairCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Prompts for a new pair's settings; null means the user canceled. Takes an optional prefill —
    /// the "Sync Selected Path..." context-menu action (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6)
    /// already knows one side of the pair, the toolbar's plain "Add pair" button passes null.
    /// </summary>
    public Func<SyncPairPrefill?, Task<NewSyncPairRequest?>>? RequestNewPairAsync { get; set; }

    /// <summary>
    /// Lists a remote folder's children, for the "Add pair" dialog's remote folder picker.
    /// Wired to <see cref="Services.Providers.IDriveOperations.ListFolderAsync"/>; left null disables
    /// the picker button (the dialog falls back to typing the path by hand).
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<DriveItem>>>? GetRemoteFolderChildren { get; set; }

    /// <summary>Shown a dry-run plan; returns true if the user chose to run it immediately. Forwarded to every row.</summary>
    public Func<SyncPlan, IReadOnlyList<string>, Task<bool>>? RequestPreviewConfirmationAsync { get; set; }

    /// <summary>Shown the parked conflicts; returns a decision per queue row. Forwarded to every row.</summary>
    public Func<IReadOnlyList<QueuedSyncAction>, Task<IReadOnlyDictionary<long, ConflictResolution>>>? RequestConflictResolutionsAsync { get; set; }

    /// <summary>Shown a pair's current direction/conflict policy; returns the new values, or null if canceled. Forwarded to every row.</summary>
    public Func<SyncPairViewModel, Task<EditSyncPairRequest?>>? RequestEditPairAsync { get; set; }

    /// <summary>
    /// A yes/no question. Returns false if no handler is attached — an unanswerable question must
    /// not be treated as consent.
    /// </summary>
    public Func<string, Task<bool>>? RequestConfirmationAsync { get; set; }

    public async Task InitializeAsync() => await LoadPairsAsync();

    /// <summary>
    /// docs/PLAN-LOCAL-SYNC.md §7's startup step: requeue work a previous run died holding, and
    /// clear its half-downloaded temp files. Called once from the app's startup path (not from
    /// <see cref="InitializeAsync"/>, which runs every time the Sync window opens — re-running
    /// this while a sync is in flight would requeue rows that genuinely *are* running).
    /// </summary>
    public async Task RecoverFromPreviousRunAsync()
    {
        if (_hasRecovered)
        {
            return;
        }

        _hasRecovered = true;

        // Every active account's own leftovers, not just the primary's — a crash affects whichever
        // account was mid-transfer, which might not be the one currently on screen.
        var clearedMessages = new List<string>();
        foreach (var slot in _slots)
        {
            var cleared = await slot.CrashRecovery.RecoverAsync();
            if (cleared > 0)
            {
                clearedMessages.Add($"{slot.DisplayName}: cleared {cleared} leftover download folder(s).");
            }

            // Only after recovery: starting the loop first could hand a cycle a queue whose
            // 'Running' rows haven't been requeued yet. And only if the user hadn't switched
            // automatic sync off in a previous run — that choice is meant to outlive the process.
            if (slot.Scheduler is not null && await slot.StateStore.GetAutomaticSyncEnabledAsync())
            {
                slot.Scheduler.Start();
            }
        }

        if (clearedMessages.Count > 0)
        {
            StatusMessage = "Recovered from a previous run: " + string.Join(" ", clearedMessages);
        }

        RaiseAutomaticSyncState();
        foreach (var toggle in AccountSyncToggles)
        {
            toggle.RaiseState();
        }
    }

    private async Task ToggleAutomaticSyncAsync()
    {
        var scheduler = Primary.Scheduler;
        if (scheduler is null)
        {
            return;
        }

        if (scheduler.IsRunning)
        {
            await scheduler.StopAsync();
            await Primary.StateStore.SetAutomaticSyncEnabledAsync(false);
            StatusMessage = "Automatic sync paused. Local changes won't be picked up until you resume it.";
        }
        else
        {
            scheduler.Start();
            await Primary.StateStore.SetAutomaticSyncEnabledAsync(true);
            StatusMessage = "Automatic sync resumed.";
        }

        RaiseAutomaticSyncState();
        // The primary slot's own AccountSyncToggleViewModel (AccountSyncToggles[0]) points at the
        // same scheduler/store this just changed — keep its displayed state in sync too, since a
        // caller might be bound to either surface.
        AccountSyncToggles[0].RaiseState();
    }

    private void RaiseAutomaticSyncState()
    {
        OnPropertyChanged(nameof(IsAutomaticSyncRunning));
        OnPropertyChanged(nameof(AutomaticSyncLabel));
    }

    private async Task LoadPairsAsync()
    {
        IsBusy = true;
        try
        {
            Pairs.Clear();
            foreach (var slot in _slots)
            {
                var pairs = await slot.StateStore.GetPairsAsync();
                foreach (var pair in pairs)
                {
                    var row = AddPairViewModel(pair, slot);
                    await row.RefreshOutstandingAsync();
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private SyncPairViewModel AddPairViewModel(SyncPair pair, AccountSlot slot)
    {
        // Only labeled once there's more than one active account — with a single account, every
        // row obviously belongs to it, and the label would be pure noise.
        var accountLabel = _slots.Count > 1 ? slot.DisplayName : "";
        var viewModel = new SyncPairViewModel(pair, slot.Executor, slot.StateStore, RemovePairViewModel, accountLabel)
        {
            RequestPreviewConfirmationAsync = RequestPreviewConfirmationAsync,
            RequestConflictResolutionsAsync = RequestConflictResolutionsAsync,
            RequestEditAsync = RequestEditPairAsync,
            OnError = message => StatusMessage = message,
        };
        Pairs.Add(viewModel);
        return viewModel;
    }

    private void RemovePairViewModel(SyncPairViewModel viewModel) => Pairs.Remove(viewModel);

    /// <summary>
    /// Rebuilds the chip row from whatever is currently in <see cref="Pairs"/> — called whenever
    /// that collection changes (a load, an add, a remove), so the counts and the set of accounts
    /// offered never drift from what's actually shown.
    /// </summary>
    private void RebuildProviderFilters()
    {
        ProviderFilters.Clear();

        // Single-account labels are blank (SyncPairViewModel.AccountLabel's own rule) — grouping by
        // that would produce one useless "" chip. _slots.Count > 1 is the same "more than one
        // account" gate AccountLabel itself uses, so this stays consistent with what the rows
        // already display.
        if (_slots.Count <= 1)
        {
            _providerFilter = null;
            OnPropertyChanged(nameof(VisiblePairs));
            OnPropertyChanged(nameof(HasProviderFilters));
            return;
        }

        if (_providerFilter is not null && Pairs.All(pair => pair.AccountLabel != _providerFilter))
        {
            // The account this filter pointed at no longer has any pairs loaded (e.g. its last pair
            // was removed) — falling back to "Todos" keeps the list from silently going empty.
            _providerFilter = null;
        }

        ProviderFilters.Add(new ProviderFilterViewModel(null, Pairs.Count, ApplyProviderFilterAsync, ReportError)
        {
            IsActive = _providerFilter is null,
        });

        foreach (var slot in _slots)
        {
            var count = Pairs.Count(pair => pair.AccountLabel == slot.DisplayName);
            ProviderFilters.Add(new ProviderFilterViewModel(slot.DisplayName, count, ApplyProviderFilterAsync, ReportError)
            {
                IsActive = _providerFilter == slot.DisplayName,
            });
        }

        OnPropertyChanged(nameof(VisiblePairs));
        OnPropertyChanged(nameof(HasProviderFilters));
    }

    private Task ApplyProviderFilterAsync(string? accountLabel)
    {
        // Clicking the active chip clears it, so the filter can always be undone from where it was
        // applied, not only from "Todos" — same behavior MainWindowViewModel's own kind filter has.
        _providerFilter = _providerFilter == accountLabel ? null : accountLabel;
        foreach (var chip in ProviderFilters)
        {
            chip.IsActive = chip.AccountLabel == _providerFilter;
        }

        OnPropertyChanged(nameof(VisiblePairs));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Public (not just <see cref="AddPairCommand"/>) so the explorer's "Sync Selected Path..."
    /// context-menu action (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6) can drive the exact same
    /// validated create flow with a prefill, rather than duplicating it.
    /// </summary>
    public async Task AddPairAsync(SyncPairPrefill? prefill = null)
    {
        var requester = RequestNewPairAsync;
        if (requester is null)
        {
            StatusMessage = "Adding a sync pair is not available.";
            return;
        }

        var request = await requester(prefill);
        if (request is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Targets ActiveSlot — the account the user was actually browsing when they opened
            // this dialog (MainWindowViewModel.SwitchBrowserAccountAsync keeps it in sync via
            // SetActiveAccount), not always whichever account was "primary" at startup. Validated
            // against what's actually in the database, not the rows currently loaded in the panel
            // — the scheduler and other windows can have added pairs since.
            var targetSlot = ActiveSlot;
            var validationError = SyncPairValidator.Validate(request.RemotePath, request.LocalPath, await targetSlot.StateStore.GetPairsAsync())
                                  ?? LocalFolderInspector.CheckWritable(request.LocalPath);
            if (validationError is not null)
            {
                StatusMessage = validationError;
                return;
            }

            if (!await ConfirmBusyFolderAsync(request, targetSlot))
            {
                StatusMessage = "Cancelled — no pair was created.";
                return;
            }

            var pair = await targetSlot.StateStore.CreatePairAsync(request.RemotePath, request.LocalPath, request.Direction, request.ConflictPolicy, mirrorDeletes: request.MirrorDeletes);
            AddPairViewModel(pair, targetSlot);
            StatusMessage = $"Added: {pair.RemotePath} {DirectionArrow(pair.Direction)} {pair.LocalPath}";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (the pair's UNIQUE(RemotePath, LocalPath))
        {
            StatusMessage = "That remote/local combination is already a sync pair.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// §12's "warn if it already contains many files". Only asked for directions that would *send*
    /// those files: a `RemoteToLocal` pair never uploads, so the existing contents are only at risk
    /// of being moved to the local trash — which the mandatory preview shows before anything happens.
    /// For the other two directions "Run now" can be pressed without ever opening the preview, so
    /// this is the only place the user would find out.
    /// </summary>
    private async Task<bool> ConfirmBusyFolderAsync(NewSyncPairRequest request, AccountSlot targetSlot)
    {
        if (request.Direction == SyncDirection.RemoteToLocal)
        {
            return true;
        }

        var count = LocalFolderInspector.CountEntriesUpTo(request.LocalPath, LocalFolderInspector.BusyFolderThreshold + 1);
        if (count is null or <= LocalFolderInspector.BusyFolderThreshold)
        {
            return true;
        }

        var confirm = RequestConfirmationAsync;
        if (confirm is null)
        {
            return true; // no way to ask; creating the pair is still gated by the preview
        }

        return await confirm(
            $"'{request.LocalPath}' already contains more than {LocalFolderInspector.BusyFolderThreshold} items. " +
            $"Syncing it in this direction will upload all of them to {targetSlot.DisplayName}. Continue?");
    }

    /// <summary>Looks up the configured pair (if any) whose remote side is <paramref name="remotePath"/> — for the cloud pane's sync badges (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6).</summary>
    public SyncPairViewModel? FindPairByRemotePath(string remotePath)
        => Pairs.FirstOrDefault(pair => PathsEqual(pair.RemotePath, remotePath));

    /// <summary>Looks up the configured pair (if any) whose local side is <paramref name="localPath"/> — for the local pane's sync badges.</summary>
    public SyncPairViewModel? FindPairByLocalPath(string localPath)
        => Pairs.FirstOrDefault(pair => PathsEqual(pair.LocalPath, localPath));

    private static bool PathsEqual(string a, string b)
        => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.Ordinal);

    private static string DirectionArrow(SyncDirection direction) => direction switch
    {
        SyncDirection.RemoteToLocal => "→",
        SyncDirection.LocalToRemote => "←",
        _ => "↔",
    };

    private void ReportError(Exception ex) => StatusMessage = $"Unexpected error: {ex.Message}";
}
