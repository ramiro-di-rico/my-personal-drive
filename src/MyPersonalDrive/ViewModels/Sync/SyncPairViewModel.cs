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

    /// <param name="accountLabel">
    /// Which active account this pair belongs to (e.g. "Proton Drive"/"OneDrive") — empty for the
    /// single-account case, where the panel shows only one account and a label would be noise.
    /// Set once at construction: a pair's account never changes without recreating the row.
    /// </param>
    public SyncPairViewModel(SyncPair pair, SyncExecutor executor, SyncStateStore stateStore, Action<SyncPairViewModel> onRemoved, string accountLabel = "")
    {
        _pair = pair;
        _executor = executor;
        _stateStore = stateStore;
        _onRemoved = onRemoved;
        AccountLabel = accountLabel;

        PreviewCommand = new AsyncCommand(PreviewAsync, () => !IsBusy, ReportError);
        SyncNowCommand = new AsyncCommand(RunAsync, () => !IsBusy, ReportError);
        RemoveCommand = new AsyncCommand(RemoveAsync, () => !IsBusy, ReportError);
        ResolveConflictsCommand = new AsyncCommand(ResolveConflictsAsync, () => !IsBusy && HasConflicts, ReportError);
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync, () => !IsBusy && HasFailures, ReportError);
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync, () => !IsBusy, ReportError);
        EditCommand = new AsyncCommand(EditAsync, () => !IsBusy, ReportError);

        UpdateStatusText();
    }

    public int Id => _pair.Id;

    public string AccountLabel { get; }

    public bool HasAccountLabel => AccountLabel.Length > 0;

    public string RemotePath => _pair.RemotePath;

    public string LocalPath => _pair.LocalPath;

    public string DirectionText => _pair.Direction switch
    {
        SyncDirection.RemoteToLocal => "Remote → Local",
        SyncDirection.LocalToRemote => "Local → Remote",
        _ => "Two-way",
    };

    public SyncDirection Direction => _pair.Direction;

    public ConflictPolicy ConflictPolicy => _pair.ConflictPolicy;

    /// <summary>True mirrors the source side exactly (deletes destination-only items); false keeps the pair additive. Only meaningful for a one-way pair — see <see cref="SyncPair.MirrorDeletes"/>.</summary>
    public bool MirrorDeletes => _pair.MirrorDeletes;

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
                ResolveConflictsCommand.RaiseCanExecuteChanged();
                RetryFailedCommand.RaiseCanExecuteChanged();
                TogglePauseCommand.RaiseCanExecuteChanged();
                EditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand SyncNowCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    public AsyncCommand ResolveConflictsCommand { get; }

    public AsyncCommand RetryFailedCommand { get; }

    public AsyncCommand TogglePauseCommand { get; }

    public AsyncCommand EditCommand { get; }

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
                OnPropertyChanged(nameof(HasFailures));
                OnPropertyChanged(nameof(ConflictText));
                ResolveConflictsCommand.RaiseCanExecuteChanged();
                RetryFailedCommand.RaiseCanExecuteChanged();
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

    public bool HasFailures
    {
        get
        {
            if (FailedCount > 0)
            {
                return true;
            }

            if (_pair.LastStatus == SyncPairStatus.Error)
            {
                return true;
            }

            if (_pair.LastStatus == SyncPairStatus.PartialFailure)
            {
                if (HasConflicts && _pair.LastError is not null && !_pair.LastError.Contains("failed") && !_pair.LastError.Contains("aborted"))
                {
                    return false;
                }

                return true;
            }

            return false;
        }
    }

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

    /// <summary>Shown this pair's current direction/conflict policy; returns the new values, or null if the user canceled.</summary>
    public Func<SyncPairViewModel, Task<EditSyncPairRequest?>>? RequestEditAsync { get; set; }

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
            await RefreshOutstandingAsync();

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
            await _stateStore.RetryFailedAsync(_pair.Id, DateTimeOffset.UtcNow);
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
        _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
        ConflictCount = (await _stateStore.GetConflictActionsAsync(_pair.Id)).Count;
        FailedCount = (await _stateStore.GetFailedActionsAsync(_pair.Id)).Count;
        OnPropertyChanged(nameof(HasFailures));
        RetryFailedCommand.RaiseCanExecuteChanged();
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
            if (_pair.LastStatus is SyncPairStatus.PartialFailure or SyncPairStatus.Error)
            {
                await _stateStore.UpdatePairStatusAsync(_pair.Id, _pair.LastSyncAt ?? DateTimeOffset.UtcNow, SyncPairStatus.Ok, null);
                _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
            }

            var msg = revived == 0
                ? "Failed state reset — sync now or resume to sync."
                : $"{revived} failed action(s) queued again — they'll run on the next sync.";
            StatusText = _pair.IsPaused ? $"Paused — {msg}" : msg;
        }
        finally
        {
            IsBusy = false;
            await RefreshOutstandingAsync();
        }
    }

    /// <summary>
    /// Changes direction/conflict policy on the existing pair rather than recreating it — the
    /// remote/local paths stay fixed, since changing those already has a working path (remove,
    /// then add a new pair) and would need re-validating against every other pair.
    /// </summary>
    private async Task EditAsync()
    {
        var requester = RequestEditAsync;
        if (requester is null)
        {
            StatusText = "Editing a pair is not available.";
            return;
        }

        var request = await requester(this);
        if (request is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _stateStore.UpdatePairSettingsAsync(_pair.Id, request.Direction, request.ConflictPolicy, request.MirrorDeletes);
            _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
            OnPropertyChanged(nameof(DirectionText));
            OnPropertyChanged(nameof(Direction));
            OnPropertyChanged(nameof(ConflictPolicy));
            OnPropertyChanged(nameof(MirrorDeletes));
            UpdateStatusText();
        }
        finally
        {
            IsBusy = false;
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
        OnPropertyChanged(nameof(HasFailures));
        RetryFailedCommand.RaiseCanExecuteChanged();
    }

    private static string FormatTime(DateTimeOffset? timestamp)
        => timestamp is { } t ? t.ToLocalTime().ToString("g") : "never";

    private void ReportError(Exception ex)
    {
        StatusText = $"Unexpected error: {ex.Message}";
        OnError?.Invoke(ex.Message);
    }
}
