using Avalonia.Threading;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// One row in the sync panel. Wraps a <see cref="SyncPair"/> plus the commands to preview,
/// run, and remove it. Kept separate from <see cref="MainWindowViewModel"/> per
/// docs/PLAN-TECH-DEBT.md's recommendation not to keep growing that file.
/// </summary>
public sealed class SyncPairViewModel : ObservableObject
{
    private readonly SyncExecutor _executor;
    private readonly SyncStateStore _stateStore;
    private readonly Action<SyncPairViewModel> _onRemoved;
    private SyncPair _pair;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private int _conflictCount;
    private int _failedCount;

    public SyncPairViewModel(SyncPair pair, SyncExecutor executor, SyncStateStore stateStore, Action<SyncPairViewModel> onRemoved)
    {
        _pair = pair;
        _executor = executor;
        _stateStore = stateStore;
        _onRemoved = onRemoved;

        PreviewCommand = new AsyncCommand(PreviewAsync, () => !IsBusy, ReportError);
        SyncNowCommand = new AsyncCommand(RunAsync, () => !IsBusy, ReportError);
        RemoveCommand = new AsyncCommand(RemoveAsync, () => !IsBusy, ReportError);
        ResolveConflictsCommand = new AsyncCommand(ResolveConflictsAsync, () => !IsBusy && HasConflicts, ReportError);
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync, () => !IsBusy && HasFailures, ReportError);
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync, () => !IsBusy, ReportError);

        UpdateStatusText();
    }

    public int Id => _pair.Id;

    public string RemotePath => _pair.RemotePath;

    public string LocalPath => _pair.LocalPath;

    public string DirectionText => _pair.Direction switch
    {
        SyncDirection.RemoteToLocal => "Remote → Local",
        SyncDirection.LocalToRemote => "Local → Remote",
        _ => "Two-way",
    };

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PreviewCommand.RaiseCanExecuteChanged();
                SyncNowCommand.RaiseCanExecuteChanged();
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand SyncNowCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    public AsyncCommand ResolveConflictsCommand { get; }

    public AsyncCommand RetryFailedCommand { get; }

    public AsyncCommand TogglePauseCommand { get; }

    /// <summary>
    /// Paused means "no automatic cycles". Preview and Sync now stay available on purpose: pausing
    /// expresses "stop doing this on your own", not "refuse my explicit instructions" — §12 lists
    /// pause and sync-now as separate controls on the same row.
    /// </summary>
    public bool IsPaused => _pair.IsPaused;

    public string PauseGlyph => IsPaused ? "▶️" : "⏸️";

    public string PauseTooltip => IsPaused
        ? "Resume automatic syncing for this pair"
        : "Pause automatic syncing for this pair (you can still sync it by hand)";

    public int ConflictCount
    {
        get => _conflictCount;
        private set
        {
            if (SetProperty(ref _conflictCount, value))
            {
                OnPropertyChanged(nameof(HasConflicts));
                OnPropertyChanged(nameof(ConflictText));
                ResolveConflictsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int FailedCount
    {
        get => _failedCount;
        private set
        {
            if (SetProperty(ref _failedCount, value))
            {
                OnPropertyChanged(nameof(HasFailures));
                RetryFailedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasConflicts => ConflictCount > 0;

    public bool HasFailures => FailedCount > 0;

    public string ConflictText => ConflictCount == 1 ? "⚠ 1 conflict" : $"⚠ {ConflictCount} conflicts";

    /// <summary>
    /// Shown a dry-run plan plus any warnings about carrying it out; returns true if the user chose
    /// to run it immediately. Warnings travel with the plan rather than being shown separately
    /// because they're part of the same decision — "not enough disk space" only means something
    /// alongside the number of bytes it refers to.
    /// </summary>
    public Func<SyncPlan, IReadOnlyList<string>, Task<bool>>? RequestPreviewConfirmationAsync { get; set; }

    /// <summary>
    /// Shown the parked conflicts; returns the user's decision per queue row. An absent entry means
    /// "leave that one alone", so closing the dialog resolves nothing.
    /// </summary>
    public Func<IReadOnlyList<QueuedSyncAction>, Task<IReadOnlyDictionary<long, ConflictResolution>>>? RequestConflictResolutionsAsync { get; set; }

    public Action<string>? OnError { get; set; }

    private async Task PreviewAsync()
    {
        var confirm = RequestPreviewConfirmationAsync;
        if (confirm is null)
        {
            StatusText = "Preview is not available.";
            return;
        }

        IsBusy = true;
        StatusText = "Scanning...";
        try
        {
            var plan = await _executor.PreviewAsync(_pair);
            UpdateStatusText();

            var warnings = new List<string>();
            if (LocalFolderInspector.CheckFreeSpace(_pair.LocalPath, plan.Stats.BytesToDownload) is { } spaceWarning)
            {
                warnings.Add(spaceWarning);
            }

            if (await confirm(plan, warnings))
            {
                await RunAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        StatusText = "Syncing...";

        // Subscribed per run rather than for the object's lifetime: the executor is shared by every
        // pair and by the scheduler, so a permanent subscription would show one pair another's
        // progress.
        void OnProgress(object? _, SyncExecutor.SyncProgress progress)
            => Dispatcher.UIThread.Post(() => StatusText = $"⟳ {progress.Describe()}");

        _executor.Progress += OnProgress;
        try
        {
            await _executor.RunAsync(_pair);
            _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
        }
        finally
        {
            _executor.Progress -= OnProgress;
            IsBusy = false;
            UpdateStatusText();
            await RefreshOutstandingAsync();
        }
    }

    /// <summary>Re-reads the parked conflicts and dead rows, so the row's badges tell the truth.</summary>
    public async Task RefreshOutstandingAsync()
    {
        ConflictCount = (await _stateStore.GetConflictActionsAsync(_pair.Id)).Count;
        FailedCount = (await _stateStore.GetFailedActionsAsync(_pair.Id)).Count;
    }

    private async Task ResolveConflictsAsync()
    {
        var requester = RequestConflictResolutionsAsync;
        if (requester is null)
        {
            StatusText = "Resolving conflicts is not available.";
            return;
        }

        var conflicts = await _stateStore.GetConflictActionsAsync(_pair.Id);
        if (conflicts.Count == 0)
        {
            await RefreshOutstandingAsync();
            return;
        }

        var decisions = await requester(conflicts);
        if (decisions.Count == 0)
        {
            return; // dialog dismissed — deciding nothing must change nothing
        }

        IsBusy = true;
        var resolved = 0;
        try
        {
            foreach (var conflict in conflicts.Where(c => decisions.ContainsKey(c.Id)))
            {
                // One at a time, and a failure on one file must not abandon the rest: each
                // resolution is an independent decision the user already made.
                try
                {
                    await _executor.ResolveConflictAsync(_pair, conflict, decisions[conflict.Id]);
                    resolved++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    OnError?.Invoke($"Could not resolve '{conflict.RelativePath}': {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshOutstandingAsync();
        }

        StatusText = resolved == conflicts.Count
            ? $"Resolved {resolved} conflict(s)."
            : $"Resolved {resolved} of {decisions.Count} chosen conflict(s); {ConflictCount} still parked.";
    }

    private async Task RetryFailedAsync()
    {
        IsBusy = true;
        try
        {
            var revived = await _stateStore.RetryFailedAsync(_pair.Id, DateTimeOffset.UtcNow);
            StatusText = revived == 0
                ? "Nothing to retry."
                : $"{revived} failed action(s) queued again — they'll run on the next sync.";
        }
        finally
        {
            IsBusy = false;
            await RefreshOutstandingAsync();
        }
    }

    private async Task RemoveAsync()
    {
        await _stateStore.DeletePairAsync(_pair.Id);
        _onRemoved(this);
    }

    private async Task TogglePauseAsync()
    {
        IsBusy = true;
        try
        {
            await _stateStore.SetPairPausedAsync(_pair.Id, !_pair.IsPaused);
            _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseGlyph));
            OnPropertyChanged(nameof(PauseTooltip));
            UpdateStatusText();
        }
    }

    private void UpdateStatusText()
    {
        var status = _pair.LastStatus switch
        {
            SyncPairStatus.Never => "Never synced",
            SyncPairStatus.Ok => $"Up to date ({FormatTime(_pair.LastSyncAt)})",
            SyncPairStatus.PartialFailure => $"Partial failure ({FormatTime(_pair.LastSyncAt)}): {_pair.LastError}",
            SyncPairStatus.Error => $"Error: {_pair.LastError}",
            _ => "Unknown",
        };

        // A paused pair saying only "Up to date" would be a lie the moment anything changes, so the
        // pause is stated first — it's the fact that decides whether the rest is still being kept true.
        StatusText = _pair.IsPaused ? $"Paused — {status}" : status;
    }

    private static string FormatTime(DateTimeOffset? timestamp)
        => timestamp is { } t ? t.ToLocalTime().ToString("g") : "never";

    private void ReportError(Exception ex)
    {
        StatusText = $"Unexpected error: {ex.Message}";
        OnError?.Invoke(ex.Message);
    }
}
