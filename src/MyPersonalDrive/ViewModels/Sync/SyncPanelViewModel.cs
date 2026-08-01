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
    private readonly SyncStateStore _stateStore;
    private readonly SyncExecutor _executor;
    private readonly SyncCrashRecovery _crashRecovery;
    private readonly SyncScheduler? _scheduler;
    private string _statusMessage = "Add a folder to start syncing it from Proton Drive.";
    private bool _isBusy;
    private bool _hasRecovered;

    public SyncPanelViewModel(SyncStateStore stateStore, SyncExecutor executor, SyncCrashRecovery crashRecovery, SyncScheduler? scheduler = null)
    {
        _stateStore = stateStore;
        _executor = executor;
        _crashRecovery = crashRecovery;
        _scheduler = scheduler;
        Pairs = new ObservableCollection<SyncPairViewModel>();

        AddPairCommand = new AsyncCommand(AddPairAsync, () => !IsBusy, ReportError);
        RefreshCommand = new AsyncCommand(LoadPairsAsync, () => !IsBusy, ReportError);
        ToggleAutomaticSyncCommand = new AsyncCommand(ToggleAutomaticSyncAsync, () => _scheduler is not null, ReportError);

        if (_scheduler is not null)
        {
            // A cycle the scheduler ran on its own still has to be reflected in the row the user
            // is looking at, or the panel would show stale "Up to date" times.
            _scheduler.PairSynced += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadPairsAsync());
            _scheduler.WatcherDegraded += (_, reason) => Dispatcher.UIThread.Post(() => StatusMessage = reason);
        }
    }

    public ObservableCollection<SyncPairViewModel> Pairs { get; }

    public AsyncCommand AddPairCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand ToggleAutomaticSyncCommand { get; }

    public bool IsAutomaticSyncRunning => _scheduler?.IsRunning ?? false;

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
        var cleared = await _crashRecovery.RecoverAsync();
        if (cleared > 0)
        {
            StatusMessage = $"Recovered from a previous run: cleared {cleared} leftover download folder(s).";
        }

        // Only after recovery: starting the loop first could hand a cycle a queue whose 'Running'
        // rows haven't been requeued yet.
        _scheduler?.Start();
        RaiseAutomaticSyncState();
    }

    private async Task ToggleAutomaticSyncAsync()
    {
        if (_scheduler is null)
        {
            return;
        }

        if (_scheduler.IsRunning)
        {
            await _scheduler.StopAsync();
            StatusMessage = "Automatic sync paused. Local changes won't be picked up until you resume it.";
        }
        else
        {
            _scheduler.Start();
            StatusMessage = "Automatic sync resumed.";
        }

        RaiseAutomaticSyncState();
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
            var pairs = await _stateStore.GetPairsAsync();
            Pairs.Clear();
            foreach (var pair in pairs)
            {
                var row = AddPairViewModel(pair);
                await row.RefreshOutstandingAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private SyncPairViewModel AddPairViewModel(SyncPair pair)
    {
        var viewModel = new SyncPairViewModel(pair, _executor, _stateStore, RemovePairViewModel)
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
            // Validated against what's actually in the database, not the rows currently loaded in
            // the panel — the scheduler and other windows can have added pairs since.
            var validationError = SyncPairValidator.Validate(request.RemotePath, request.LocalPath, await _stateStore.GetPairsAsync())
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

            var pair = await _stateStore.CreatePairAsync(request.RemotePath, request.LocalPath, request.Direction, request.ConflictPolicy);
            AddPairViewModel(pair);
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
            "Syncing it in this direction will upload all of them to Proton Drive. Continue?");
    }

    private static string DirectionArrow(SyncDirection direction) => direction switch
    {
        SyncDirection.RemoteToLocal => "→",
        SyncDirection.LocalToRemote => "←",
        _ => "↔",
    };

    private void ReportError(Exception ex) => StatusMessage = $"Unexpected error: {ex.Message}";
}
