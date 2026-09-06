using Avalonia.Threading;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

using MyPersonalDrive.Services.Localization;

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

    /// <summary>Stamps retries and status updates; tests substitute a fake clock (docs/PLAN-UX-ROUND-4.md Z4).</summary>
    private readonly TimeProvider _timeProvider;
    private readonly Action<SyncPairViewModel> _onRemoved;
    private SyncPair _pair;
    private LocalizedText _status = LocalizedText.None;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private int _conflictCount;
    private int _failedCount;

    /// <param name="accountLabel">
    /// Which active account this pair belongs to (e.g. "Proton Drive"/"OneDrive") — empty for the
    /// single-account case, where the panel shows only one account and a label would be noise.
    /// Set once at construction: a pair's account never changes without recreating the row.
    /// </param>
    public SyncPairViewModel(SyncPair pair, SyncExecutor executor, SyncStateStore stateStore, Action<SyncPairViewModel> onRemoved, string accountLabel = "", TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        ReviewFailuresCommand = new AsyncCommand(ReviewFailuresAsync, () => !IsBusy && HasFailures, ReportError);
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync, () => !IsBusy, ReportError);
        EditCommand = new AsyncCommand(EditAsync, () => !IsBusy, ReportError);

        UpdateStatusText();
    }

    public int Id => _pair.Id;

    public string AccountLabel { get; }

    public bool HasAccountLabel => AccountLabel.Length > 0;

    public string RemotePath => _pair.RemotePath;

    public string LocalPath => _pair.LocalPath;

    public string DirectionText => Loc.T(_pair.Direction switch
    {
        SyncDirection.RemoteToLocal => StringKeys.Sync.DirectionRemoteToLocal,
        SyncDirection.LocalToRemote => StringKeys.Sync.DirectionLocalToRemote,
        _ => StringKeys.Sync.DirectionTwoWay,
    });

    public SyncDirection Direction => _pair.Direction;

    public ConflictPolicy ConflictPolicy => _pair.ConflictPolicy;

    /// <summary>True mirrors the source side exactly (deletes destination-only items); false keeps the pair additive. Only meaningful for a one-way pair — see <see cref="SyncPair.MirrorDeletes"/>.</summary>
    public bool MirrorDeletes => _pair.MirrorDeletes;

    /// <summary>
    /// The row's status line. Deferred, like the two explorer panes' — a row saying "Up to date"
    /// sits there for as long as nothing changes, which is exactly the case that must not freeze in
    /// the old language (docs/PLAN-I18N.md §6.3).
    /// </summary>
    public string StatusText => _statusMessage;

    /// <summary>The unrendered form, so tests can assert on a key instead of on prose.</summary>
    internal LocalizedText StatusTextValue => _status;

    private void SetStatus(LocalizedText text)
    {
        _status = text;
        SetProperty(ref _statusMessage, text.Render(), nameof(StatusText));
    }

    private void SetStatus(string key, params object?[] args) => SetStatus(LocalizedText.Of(key, args));

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
                ReviewFailuresCommand.RaiseCanExecuteChanged();
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

    /// <summary>
    /// Opens the per-action failure list (docs/PLAN-UX-ROUND-2.md §6). Distinct from
    /// <see cref="RetryFailedCommand"/>, which is still the one-click "retry everything" — this is
    /// the "what actually failed, and why" the row never offered.
    /// </summary>
    public AsyncCommand ReviewFailuresCommand { get; }

    public AsyncCommand TogglePauseCommand { get; }

    public AsyncCommand EditCommand { get; }

    /// <summary>
    /// Paused means "no automatic cycles". Preview and Sync now stay available on purpose: pausing
    /// expresses "stop doing this on your own", not "refuse my explicit instructions" — §12 lists
    /// pause and sync-now as separate controls on the same row.
    /// </summary>
    public bool IsPaused => _pair.IsPaused;

    public string PauseGlyph => IsPaused ? "▶️" : "⏸️";

    public string PauseTooltip => Loc.T(IsPaused
        ? StringKeys.Sync.PauseResumeTooltip
        : StringKeys.Sync.PausePauseTooltip);

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
                ReviewFailuresCommand.RaiseCanExecuteChanged();
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
                OnPropertyChanged(nameof(FailureSummary));
                RetryFailedCommand.RaiseCanExecuteChanged();
                ReviewFailuresCommand.RaiseCanExecuteChanged();
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

            // PartialFailure with conflicts and no failed rows is a conflicts-only outcome:
            // FailedCount above already answered that authoritatively from the durable queue.
            // This used to second-guess it by substring-matching LastError for failure wording,
            // which broke the moment the message was reworded — as U4's translation proved
            // (docs/PLAN-UX-ROUND-2.md §6).
            return _pair.LastStatus == SyncPairStatus.PartialFailure && !HasConflicts;
        }
    }

    public string ConflictText => Loc.Plural(StringKeys.Sync.ConflictsCount, ConflictCount);

    /// <summary>Label for the button that opens the per-action failure list.</summary>
    public string FailureSummary => Loc.Plural(StringKeys.Sync.FailuresSummary, FailedCount);

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

    /// <summary>Shown the failed queue rows; returns a decision per row. Left null disables the failures view.</summary>
    public Func<IReadOnlyList<SyncFailureViewModel>, Task<IReadOnlyDictionary<long, SyncFailureDecision>>>? RequestFailureReviewAsync { get; set; }

    /// <summary>Shown this pair's current direction/conflict policy; returns the new values, or null if the user canceled.</summary>
    public Func<SyncPairViewModel, Task<EditSyncPairRequest?>>? RequestEditAsync { get; set; }

    /// <summary>
    /// Re-runs <see cref="SyncPairValidator"/>'s shared-local-folder rule against a proposed new
    /// direction, before <see cref="EditAsync"/> applies it — see
    /// <see cref="SyncPairValidator.ValidateDirectionChange"/>. Returns the error message to show,
    /// or null when the change is safe. Left null disables the check (e.g. in tests that don't
    /// care about it), the same way every other optional delegate on this type does.
    /// </summary>
    public Func<SyncDirection, Task<SyncPairIssue?>>? ValidateDirectionChangeAsync { get; set; }

    public Action<string>? OnError { get; set; }

    private async Task PreviewAsync()
    {
        var confirm = RequestPreviewConfirmationAsync;
        if (confirm is null)
        {
            SetStatus(StringKeys.Sync.PreviewUnavailable);
            return;
        }

        IsBusy = true;
        SetStatus(StringKeys.Sync.Analyzing);
        try
        {
            var plan = await _executor.PreviewAsync(_pair);
            UpdateStatusText();
            await RefreshOutstandingAsync();

            var warnings = new List<string>();
            if (LocalFolderInspector.CheckFreeSpace(_pair.LocalPath, plan.Stats.BytesToDownload) is { } spaceWarning)
            {
                warnings.Add(SyncIssuePresenter.Describe(spaceWarning).Render());
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
        SetStatus(StringKeys.Sync.Syncing);

        // Subscribed per run rather than for the object's lifetime: the executor is shared by every
        // pair and by the scheduler, so a permanent subscription would show one pair another's
        // progress.
        void OnProgress(object? _, SyncExecutor.SyncProgress progress)
            => Dispatcher.UIThread.Post(() => SetStatus(LocalizedText.Of(StringKeys.Sync.Progress, progress.Describe())));

        _executor.Progress += OnProgress;
        try
        {
            await _stateStore.RetryFailedAsync(_pair.Id, _timeProvider.GetUtcNow());
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
        OnPropertyChanged(nameof(FailureSummary));
        RetryFailedCommand.RaiseCanExecuteChanged();
        ReviewFailuresCommand.RaiseCanExecuteChanged();
    }

    private async Task ResolveConflictsAsync()
    {
        var requester = RequestConflictResolutionsAsync;
        if (requester is null)
        {
            SetStatus(StringKeys.Sync.ConflictsUnavailable);
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
                    OnError?.Invoke(Loc.F(StringKeys.Sync.ConflictsResolveFailed, conflict.RelativePath, ex.DescribeForUser().Render()));
                }
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshOutstandingAsync();
        }

        SetStatus(resolved == conflicts.Count
            ? LocalizedText.Plural(StringKeys.Sync.ConflictsResolved, resolved)
            : LocalizedText.Of(StringKeys.Sync.ConflictsResolvedPartial, resolved, decisions.Count, ConflictCount));
    }

    private async Task RetryFailedAsync()
    {
        IsBusy = true;
        try
        {
            var revived = await _stateStore.RetryFailedAsync(_pair.Id, _timeProvider.GetUtcNow());
            if (_pair.LastStatus is SyncPairStatus.PartialFailure or SyncPairStatus.Error)
            {
                await _stateStore.UpdatePairStatusAsync(_pair.Id, _pair.LastSyncAt ?? _timeProvider.GetUtcNow(), SyncPairStatus.Ok, null);
                _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
            }

            var msg = revived == 0
                ? Loc.T(StringKeys.Sync.RetryReset)
                : Loc.Plural(StringKeys.Sync.RetryRequeued, revived);
            SetStatus(_pair.IsPaused ? LocalizedText.Of(StringKeys.Sync.PausedPrefix, msg) : LocalizedText.Verbatim(msg));
        }
        finally
        {
            IsBusy = false;
            await RefreshOutstandingAsync();
        }
    }

    /// <summary>
    /// Shows what actually failed and lets the user decide per action, instead of the blind
    /// retry-everything the row offered before (docs/PLAN-UX-ROUND-2.md §6). Deliberately the same
    /// shape as <see cref="ResolveConflictsAsync"/>: gather the rows, hand them to the view, apply
    /// only what came back.
    /// </summary>
    private async Task ReviewFailuresAsync()
    {
        var requester = RequestFailureReviewAsync;
        if (requester is null)
        {
            SetStatus(StringKeys.Sync.FailuresUnavailable);
            return;
        }

        var failures = await _stateStore.GetFailedActionsAsync(_pair.Id);
        if (failures.Count == 0)
        {
            await RefreshOutstandingAsync();
            return;
        }

        var decisions = await requester(failures.Select(f => new SyncFailureViewModel(f)).ToList());
        if (decisions.Count == 0)
        {
            return; // dialog dismissed — deciding nothing must change nothing
        }

        IsBusy = true;
        try
        {
            var toRetry = decisions.Where(d => d.Value == SyncFailureDecision.Retry).Select(d => d.Key).ToList();
            var toDiscard = decisions.Where(d => d.Value == SyncFailureDecision.Discard).Select(d => d.Key).ToList();

            var retried = await _stateStore.RetryFailedAsync(_pair.Id, toRetry, _timeProvider.GetUtcNow());
            var discarded = await _stateStore.DiscardFailedAsync(_pair.Id, toDiscard);

            // Only clear the pair's error banner once nothing failed is left behind; a partial
            // decision must not make the row claim it is healthy.
            if (retried + discarded == failures.Count && _pair.LastStatus is SyncPairStatus.PartialFailure or SyncPairStatus.Error)
            {
                await _stateStore.UpdatePairStatusAsync(_pair.Id, _pair.LastSyncAt ?? _timeProvider.GetUtcNow(), SyncPairStatus.Ok, null);
                _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
            }

            SetStatus(LocalizedText.Verbatim(DescribeFailureDecisions(retried, discarded)));
        }
        finally
        {
            IsBusy = false;
            await RefreshOutstandingAsync();
        }
    }

    private static string DescribeFailureDecisions(int retried, int discarded)
    {
        var localizer = Localizer.Instance;
        var parts = new List<string>();
        if (retried > 0)
        {
            parts.Add(localizer.Plural(StringKeys.Sync.FailuresRetried, retried));
        }

        if (discarded > 0)
        {
            parts.Add(localizer.Plural(StringKeys.Sync.FailuresDiscarded, discarded));
        }

        return parts.Count == 0
            ? localizer.T(StringKeys.Sync.FailuresNoChange)
            : string.Join("; ", parts) + ".";
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
            SetStatus(StringKeys.Sync.EditUnavailable);
            return;
        }

        var request = await requester(this);
        if (request is null)
        {
            return;
        }

        var validate = ValidateDirectionChangeAsync;
        if (validate is not null && await validate(request.Direction) is { } issue)
        {
            var described = SyncIssuePresenter.Describe(issue);
            SetStatus(described);
            OnError?.Invoke(described.Render());
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
            SyncPairStatus.Never => LocalizedText.Of(StringKeys.Sync.StatusNever),
            SyncPairStatus.Ok => LocalizedText.Of(StringKeys.Sync.StatusUpToDate, FormatTime(_pair.LastSyncAt)),
            SyncPairStatus.PartialFailure => LocalizedText.Of(StringKeys.Sync.StatusPartialFailure, FormatTime(_pair.LastSyncAt), _pair.LastError),
            SyncPairStatus.Error => LocalizedText.Of(StringKeys.Sync.StatusError, _pair.LastError),
            _ => LocalizedText.Of(StringKeys.Sync.StatusUnknown),
        };

        // A paused pair saying only "Up to date" would be a lie the moment anything changes, so the
        // pause is stated first — it's the fact that decides whether the rest is still being kept true.
        SetStatus(_pair.IsPaused ? LocalizedText.Of(StringKeys.Sync.PausedPrefix, status.Render()) : status);
        OnPropertyChanged(nameof(HasFailures));
        RetryFailedCommand.RaiseCanExecuteChanged();
    }

    private static string FormatTime(DateTimeOffset? timestamp)
        => timestamp is { } t ? t.ToLocalTime().ToString("g", Localizer.Instance.Culture) : Localizer.Instance.T(StringKeys.Sync.TimeNever);

    private void ReportError(Exception ex)
    {
        SetStatus(StringKeys.Status.UnexpectedError, ex.DescribeForUser().Render());
        OnError?.Invoke(ex.DescribeForUser().Render());
    }
}
