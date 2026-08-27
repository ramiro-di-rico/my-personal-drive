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

    private AccountSlot Primary => _slots[0];

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
        AccountSyncToggles = new ObservableCollection<AccountSyncToggleViewModel>();

        AddPairCommand = new AsyncCommand(AddPairAsync, () => !IsBusy, ReportError);
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

    /// <summary>Prompts for a new pair's settings; null means the user canceled.</summary>
    public Func<Task<NewSyncPairRequest?>>? RequestNewPairAsync { get; set; }

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
            OnError = message => StatusMessage = message,
        };
        Pairs.Add(viewModel);
        return viewModel;
    }

    private void RemovePairViewModel(SyncPairViewModel viewModel) => Pairs.Remove(viewModel);

    private async Task AddPairAsync()
    {
        var requester = RequestNewPairAsync;
        if (requester is null)
        {
            StatusMessage = "Adding a sync pair is not available.";
            return;
        }

        var request = await requester();
        if (request is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // A new pair always targets the primary account — GetRemoteFolderChildren (the "Add
            // pair" dialog's remote picker) is wired to the primary provider, so that's whichever
            // account's remote folder the user was actually looking at when they picked it.
            // Validated against what's actually in the database, not the rows currently loaded in
            // the panel — the scheduler and other windows can have added pairs since.
            var validationError = SyncPairValidator.Validate(request.RemotePath, request.LocalPath, await Primary.StateStore.GetPairsAsync())
                                  ?? LocalFolderInspector.CheckWritable(request.LocalPath);
            if (validationError is not null)
            {
                StatusMessage = validationError;
                return;
            }

            if (!await ConfirmBusyFolderAsync(request))
            {
                StatusMessage = "Cancelled — no pair was created.";
                return;
            }

            var pair = await Primary.StateStore.CreatePairAsync(request.RemotePath, request.LocalPath, request.Direction, request.ConflictPolicy);
            AddPairViewModel(pair, Primary);
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
    private async Task<bool> ConfirmBusyFolderAsync(NewSyncPairRequest request)
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
            $"Syncing it in this direction will upload all of them to {Primary.DisplayName}. Continue?");
    }

    private static string DirectionArrow(SyncDirection direction) => direction switch
    {
        SyncDirection.RemoteToLocal => "→",
        SyncDirection.LocalToRemote => "←",
        _ => "↔",
    };

    private void ReportError(Exception ex) => StatusMessage = $"Unexpected error: {ex.Message}";
}
