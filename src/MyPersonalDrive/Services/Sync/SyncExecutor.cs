using System.Globalization;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Scans both sides of a pair, reconciles, enqueues the plan durably, and executes it against
/// the local filesystem and the CLI, updating the three-way baseline as it goes. All three
/// directions are supported (docs/PLAN-LOCAL-SYNC.md F2); only <see cref="SyncDirection.TwoWay"/>
/// consults and maintains a baseline, since a one-way mirror doesn't need one — its source side
/// is authoritative by definition.
/// </summary>
public sealed class SyncExecutor
{
    /// <summary>
    /// How long a finished queue row is kept. Long enough to be useful when diagnosing "what did it
    /// do overnight", short enough that the table doesn't grow with uptime.
    /// </summary>
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// How long log entries are kept, and how many per pair. The count is the limit that actually
    /// bounds the table — the age limit only stops a quiet pair's stale history lingering. 1000 rows
    /// is far more than the UI shows at once and still a trivially small table.
    /// </summary>
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(30);
    private const int MaxLogEntriesPerPair = 1000;

    private readonly IDriveOperations _operations;
    private readonly IContentHasher _hasher;
    private readonly RemoteHashAlgorithm _remoteHashAlgorithm;
    private readonly SyncStateStore _stateStore;
    private readonly ILocalScanner _localScanner;
    private readonly IRemoteScanner _remoteScanner;
    private readonly IRemoteScanner? _deltaScanner;
    private readonly TimeProvider _timeProvider;
    private readonly SyncEchoSuppressor _echoSuppressor;

    /// <param name="hasher">
    /// Computes local content hashes. Defaults to <see cref="Sha1ContentHasher"/> — correct as
    /// long as the active provider is Proton; the composition root should pass one matching
    /// <c>Capabilities.RemoteHash</c> once a second provider exists (docs/PLAN-CLOUD-PROVIDERS.md P3/P6).
    /// </param>
    /// <param name="remoteHashAlgorithm">
    /// Tags fingerprints built from remote data — see the mismatch guard in
    /// <see cref="SyncReconciler"/>. Defaults to <see cref="RemoteHashAlgorithm.Sha1"/>, Proton's algorithm.
    /// </param>
    /// <param name="deltaScanner">
    /// A delta-based scanner (<see cref="DeltaRemoteScanner"/>) for providers whose
    /// <c>Capabilities.SupportsDelta</c> is true, used only for <see cref="SyncDirection.TwoWay"/>
    /// pairs — a one-way pair never populates a baseline (<see cref="LoadBaselineAsync"/> returns an
    /// empty dictionary for it), so a delta scanner has nothing to merge onto and would be unsound.
    /// Null falls back to <paramref name="remoteScanner"/> for every pair, same as before P8
    /// (docs/PLAN-CLOUD-PROVIDERS.md P8).
    /// </param>
    public SyncExecutor(
        IDriveOperations operations,
        SyncStateStore stateStore,
        ILocalScanner localScanner,
        IRemoteScanner remoteScanner,
        TimeProvider? timeProvider = null,
        SyncEchoSuppressor? echoSuppressor = null,
        IContentHasher? hasher = null,
        RemoteHashAlgorithm remoteHashAlgorithm = RemoteHashAlgorithm.Sha1,
        IRemoteScanner? deltaScanner = null)
    {
        _operations = operations;
        _hasher = hasher ?? new Sha1ContentHasher();
        _remoteHashAlgorithm = remoteHashAlgorithm;
        _stateStore = stateStore;
        _localScanner = localScanner;
        _remoteScanner = remoteScanner;
        _deltaScanner = deltaScanner;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Shared across runs by design: the whole point is to remember a deletion from the run
        // that performed it into the run that scans afterwards.
        _echoSuppressor = echoSuppressor ?? new SyncEchoSuppressor(_timeProvider);
    }

    /// <summary>
    /// Scans and reconciles without touching the filesystem or Proton Drive — the dry-run preview
    /// from docs/PLAN-LOCAL-SYNC.md §12, meant to be shown to the user before the first sync of a
    /// pair (and on request afterward).
    ///
    /// Does clear stale <c>Failed</c> rows (see <see cref="SyncStateStore.ClearStaleFailedActionsAsync"/>),
    /// even though nothing else here is a write: a preview that finds nothing to do is exactly the
    /// moment a user checks after fixing a failure by hand, and the "Retry failed actions" badge
    /// must not keep reporting a failure this same scan just proved is gone.
    /// </summary>
    public async Task<SyncPlan> PreviewAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        var baseline = await LoadBaselineAsync(pair, cancellationToken);
        var (local, remote, _) = await ScanBothSidesAsync(pair, baseline, cancellationToken);
        var plan = SyncReconciler.Reconcile(pair.Id, pair.Direction, pair.ConflictPolicy, local, remote,
            baseline, _timeProvider.GetUtcNow(), mirrorDeletes: pair.MirrorDeletes);
        await _stateStore.ClearStaleFailedActionsAsync(pair.Id, plan.Actions, cancellationToken);
        return plan;
    }

    /// <summary>
    /// Reports how far a run has got — docs/PLAN-LOCAL-SYNC.md §12's "⟳ Syncing 12/48". Per *action*,
    /// not per byte: Appendix A #12 never established whether the CLI emits parseable transfer
    /// progress, and action counts need none of that while still answering the question a user
    /// actually has, which is whether anything is happening.
    /// </summary>
    public sealed record SyncProgress(int Completed, int Total, SyncOperation? Operation, string? RelativePath)
    {
        public string Describe() => Operation is null
            ? Localizer.Instance.Plural(StringKeys.Sync.ExecScanning, Total)
            : Localizer.Instance.F(StringKeys.Sync.ExecProgress, Completed, Total, Operation, RelativePath);
    }

    /// <summary>Raised on the executing thread; a UI subscriber must marshal to its own thread.</summary>
    public event EventHandler<SyncProgress>? Progress;

    /// <summary>Scans, reconciles, enqueues the plan durably, then executes it.</summary>
    public async Task<SyncPlan> RunAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        // Housekeeping first, before anything that can fail. Both tables grow with uptime once sync
        // is automatic, and a pair whose scan throws every cycle would otherwise never prune at all
        // — precisely the pair generating the most log noise.
        await PruneHousekeepingAsync(cancellationToken);

        var baseline = await LoadBaselineAsync(pair, cancellationToken);
        var (local, remote, mapper) = await ScanBothSidesAsync(pair, baseline, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var plan = SyncReconciler.Reconcile(pair.Id, pair.Direction, pair.ConflictPolicy, local, remote,
            baseline, now, mirrorDeletes: pair.MirrorDeletes);

        await _stateStore.EnqueueActionsAsync(pair.Id, plan.Actions, now, cancellationToken);

        // A Failed row is only revived above when the plan re-proposes the exact same action; one
        // whose difference disappeared some other way (fixed by hand, another client, the file just
        // vanishing) is neither revived nor removed. Left alone it would keep inflating
        // GetFailedActionsAsync forever, disagreeing with a fresh preview that finds nothing to do.
        await _stateStore.ClearStaleFailedActionsAsync(pair.Id, plan.Actions, cancellationToken);

        // Conflicts the reconciler left unresolved (the `Ask` policy) become durable 'Conflict'
        // rows rather than being dropped on the floor — §5.6. Stale ones are cleared first: a
        // difference resolved by any means at all (the panel, an edit by hand, another client) must
        // stop being reported, and nothing else ever removes a Conflict row.
        var unresolved = pair.ConflictPolicy == ConflictPolicy.Ask ? plan.Conflicts : [];
        await _stateStore.ClearStaleConflictsAsync(pair.Id, unresolved.Select(c => c.RelativePath).ToList(), cancellationToken);
        await _stateStore.EnqueueConflictsAsync(pair.Id, unresolved, now, cancellationToken);

        var context = new RunContext(pair, mapper, local, remote,
            pair.Direction == SyncDirection.TwoWay ? new SyncBaselineWriter(_operations, _hasher, _remoteHashAlgorithm, _stateStore, mapper, pair.Id) : null);
        context.Baseline?.SeedFromScan(remote);

        var (failureCount, aborted) = await DrainQueueAsync(context, cancellationToken);
        var failedActions = await _stateStore.GetFailedActionsAsync(pair.Id, cancellationToken);
        var totalFailures = Math.Max(failureCount, failedActions.Count);

        var status = (totalFailures, unresolved.Count) switch
        {
            (0, 0) => SyncPairStatus.Ok,
            _ => SyncPairStatus.PartialFailure,
        };
        var error = BuildStatusMessage(totalFailures, unresolved.Count, aborted);
        await _stateStore.UpdatePairStatusAsync(pair.Id, _timeProvider.GetUtcNow(), status, error, cancellationToken);

        return plan;
    }

    private static string? BuildStatusMessage(int failureCount, int conflictCount, bool aborted)
    {
        var localizer = Localizer.Instance;
        var parts = new List<string>();
        if (failureCount > 0)
        {
            parts.Add(localizer.Plural(StringKeys.Sync.ExecFailed, failureCount));
        }

        if (conflictCount > 0)
        {
            parts.Add(localizer.Plural(StringKeys.Sync.ExecConflicts, conflictCount));
        }

        if (aborted)
        {
            parts.Add(localizer.T(StringKeys.Sync.ExecAborted));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Works the durable queue in priority order. A row that fails transiently is scheduled for
    /// a later attempt (<see cref="SyncRetryPolicy"/>) instead of being retried in a tight loop —
    /// the retry lands on the next run, which is what makes the backoff meaningful for a queue
    /// that outlives the process.
    /// </summary>
    private async Task<(int FailureCount, bool Aborted)> DrainQueueAsync(RunContext context, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        var pending = await _stateStore.GetPendingActionsAsync(context.Pair.Id, _timeProvider.GetUtcNow(), cancellationToken);
        var completed = 0;

        foreach (var queuedAction in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _stateStore.MarkRunningAsync(queuedAction.Id, cancellationToken);

            // Reported before the work, not after: each action can take seconds, and a counter that
            // only moves on completion leaves the slowest item invisible for exactly as long as it is
            // the one the user is waiting on.
            Progress?.Invoke(this, new SyncProgress(completed, pending.Count, queuedAction.Operation, queuedAction.RelativePath));

            try
            {
                await ExecuteOneAsync(context, queuedAction, cancellationToken);
                var completedAt = _timeProvider.GetUtcNow();
                await _stateStore.MarkDoneAsync(queuedAction.Id, completedAt, cancellationToken);
                await LogAsync(context, SyncLogLevel.Info, queuedAction.RelativePath, $"{queuedAction.Operation} completed.", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failureCount++;
                var failedAt = _timeProvider.GetUtcNow();
                var nextAttemptAt = SyncRetryPolicy.NextAttemptAt(ex, queuedAction.AttemptCount, failedAt);
                await _stateStore.MarkFailedAsync(queuedAction.Id, ex.Message, nextAttemptAt, cancellationToken);

                var retryNote = nextAttemptAt is null ? "will not retry" : $"retry after {nextAttemptAt:HH:mm:ss}";
                await LogAsync(context, SyncLogLevel.Error, queuedAction.RelativePath,
                    $"{queuedAction.Operation} failed ({retryNote}): {ex.Message}", cancellationToken);

                if (SyncRetryPolicy.ShouldAbortRun(ex))
                {
                    await LogAsync(context, SyncLogLevel.Warning, null,
                        "Stopping this run: every remaining action would fail the same way.", cancellationToken);
                    return (failureCount, true);
                }
            }

            // Counted whether it succeeded or failed: this measures progress through the queue, not
            // successes, and a failing action is just as done being attempted.
            completed++;
        }

        if (pending.Count > 0)
        {
            Progress?.Invoke(this, new SyncProgress(completed, pending.Count, null, null));
        }

        return (failureCount, false);
    }

    /// <summary>
    /// Keeps the two tables that grow on their own in check. Deliberately best-effort: housekeeping
    /// failing is not a reason to refuse to sync.
    /// </summary>
    private async Task PruneHousekeepingAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        try
        {
            await _stateStore.PruneCompletedAsync(now - CompletedRetention, cancellationToken);
            await _stateStore.PruneLogsAsync(now - LogRetention, MaxLogEntriesPerPair, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to report to the user: the sync itself is unaffected, and reporting it would
            // mean writing to the very table we just failed to tidy.
        }
    }

    private Task LogAsync(RunContext context, SyncLogLevel level, string? relativePath, string message, CancellationToken cancellationToken)
        => _stateStore.LogAsync(context.Pair.Id, level, relativePath, message, _timeProvider.GetUtcNow(), cancellationToken);

    private async Task<IReadOnlyDictionary<string, SyncBaselineEntry>> LoadBaselineAsync(SyncPair pair, CancellationToken cancellationToken)
        => pair.Direction == SyncDirection.TwoWay
            ? await _stateStore.GetBaselineAsync(pair.Id, cancellationToken)
            : new Dictionary<string, SyncBaselineEntry>();

    /// <summary>
    /// Carries out one parked conflict's resolution (§5.6's manual path) and marks the row done.
    ///
    /// Deliberately does *not* run a full cycle. Resolving one file should not cost a whole-tree
    /// remote scan — that's ~3.5s per folder (Appendix A #11a) for a decision about a single path —
    /// and the user has just told us what they want, so there is nothing left to reconcile. Only
    /// the conflicting file's own parent folder is re-listed, to get the fingerprint a download
    /// needs for its mtime.
    /// </summary>
    public async Task ResolveConflictAsync(SyncPair pair, QueuedSyncAction conflict, ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        var parent = ParentOf(conflict.RelativePath);
        var remote = new Dictionary<string, NodeFingerprint>(StringComparer.Ordinal);

        try
        {
            foreach (var item in await _operations.ListFolderAsync(mapper.ToRemoteAbsolute(parent), cancellationToken))
            {
                var relativePath = parent.Length == 0 ? item.Name : $"{parent}/{item.Name}";
                remote[relativePath] = new NodeFingerprint(relativePath, item.IsFolder, item.Size, item.ModifiedAt, item.NodeId, item.ContentHash,
                    item.ContentHash is null ? null : _remoteHashAlgorithm);
            }
        }
        catch (DriveException) when (resolution == ConflictResolution.KeepLocal)
        {
            // Keeping the local version doesn't need to know anything about the remote one.
        }

        var context = new RunContext(pair, mapper, new Dictionary<string, NodeFingerprint>(), remote,
            pair.Direction == SyncDirection.TwoWay ? new SyncBaselineWriter(_operations, _hasher, _remoteHashAlgorithm, _stateStore, mapper, pair.Id) : null);
        context.Baseline?.SeedFromScan(remote);

        var now = _timeProvider.GetUtcNow();
        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                await UploadFileAsync(context, conflict.RelativePath, cancellationToken);
                break;

            case ConflictResolution.KeepRemote:
                await DownloadFileAsync(context, conflict.RelativePath, cancellationToken);
                break;

            case ConflictResolution.KeepBoth:
                // The conflict row carries no copy name (it was parked before anyone chose), so
                // stamp one now, from the moment of the decision.
                var keepBothAction = conflict with { SecondaryPath = BuildConflictCopyPath(conflict.RelativePath, now) };
                await ResolveConflictKeepBothAsync(context, keepBothAction, cancellationToken);
                break;
        }

        if (context.Baseline is not null)
        {
            await context.Baseline.RecordAsync(conflict.RelativePath, isFolder: false, now, cancellationToken);
        }

        await _stateStore.MarkConflictResolvedAsync(conflict.Id, resolution, now, cancellationToken);
        await LogAsync(context, SyncLogLevel.Info, conflict.RelativePath, $"Conflict resolved by the user: {resolution}.", cancellationToken);
    }

    /// <summary>
    /// Same naming as the reconciler's automatic KeepBoth, so a file resolved by hand is
    /// indistinguishable from one resolved by policy.
    /// </summary>
    private static string BuildConflictCopyPath(string relativePath, DateTimeOffset timestamp)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        var directory = lastSlash < 0 ? string.Empty : relativePath[..lastSlash];
        var fileName = lastSlash < 0 ? relativePath : relativePath[(lastSlash + 1)..];
        var dot = fileName.LastIndexOf('.');
        var baseName = dot > 0 ? fileName[..dot] : fileName;
        var extension = dot > 0 ? fileName[dot..] : string.Empty;
        var stamped = $"{baseName} (local conflict {timestamp:yyyy-MM-dd HH-mm-ss}){extension}";
        return directory.Length == 0 ? stamped : $"{directory}/{stamped}";
    }

    /// <summary>
    /// Regression: this used to be one hardcoded string assuming the only skip reason was an
    /// unmappable name. Once P3 added <see cref="NodeSkipReason.CaseCollision"/>, that message
    /// became actively misleading for a case-insensitive provider's collisions — see the P1-P5
    /// adversarial review.
    /// </summary>
    private static string DescribeSkip(NodeSkip skip)
        => Localizer.Instance.F(
            skip.Reason switch
            {
                NodeSkipReason.UnmappableName => StringKeys.Sync.SkipUnmappableName,
                NodeSkipReason.CaseCollision => StringKeys.Sync.SkipCaseCollision,
                NodeSkipReason.DuplicateName => StringKeys.Sync.SkipDuplicateName,
                NodeSkipReason.GoogleNativeFile => StringKeys.Sync.SkipGoogleNativeFile,
                _ => StringKeys.Sync.SkipUnspecified,
            },
            skip.Name);

    private async Task<(IReadOnlyDictionary<string, NodeFingerprint> Local, IReadOnlyDictionary<string, NodeFingerprint> Remote, PathMapper Mapper)> ScanBothSidesAsync(
        SyncPair pair, IReadOnlyDictionary<string, SyncBaselineEntry> baseline, CancellationToken cancellationToken)
    {
        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        var exclusions = new ExclusionMatcher(pair.ExcludeGlobs);

        Directory.CreateDirectory(pair.LocalPath);
        var local = await _localScanner.ScanAsync(pair.LocalPath, exclusions, cancellationToken);

        // A one-way pair never populates a baseline (LoadBaselineAsync returns an empty dictionary
        // for it), so a delta scanner has no merge base and would be unsound — it always falls back
        // to the full-walk scanner regardless of what the provider supports.
        var scanner = pair.Direction == SyncDirection.TwoWay ? _deltaScanner ?? _remoteScanner : _remoteScanner;

        // Subscribed per scan: a node the scanner had to leave out is only discoverable here, and a
        // file visible in Proton Drive but never in the synced folder needs an explanation.
        var skipped = new List<NodeSkip>();
        void OnSkipped(object? _, NodeSkip skip) => skipped.Add(skip);
        scanner.NodeSkipped += OnSkipped;
        IReadOnlyDictionary<string, NodeFingerprint> remote;
        try
        {
            remote = await scanner.ScanAsync(pair.RemotePath, mapper, exclusions, baseline, pair.Id, cancellationToken);
        }
        finally
        {
            scanner.NodeSkipped -= OnSkipped;
        }

        foreach (var skip in skipped.DistinctBy(s => s.Name, StringComparer.Ordinal))
        {
            await _stateStore.LogAsync(pair.Id, SyncLogLevel.Warning, skip.Name, DescribeSkip(skip),
                _timeProvider.GetUtcNow(), cancellationToken);
        }

        // Applied to both sides' scans, and to the preview as much as to the run — otherwise the
        // dry-run would offer to download a file this engine just deleted (Appendix A #15).
        local = _echoSuppressor.Filter(pair.Id, SyncSide.Local, local);
        remote = _echoSuppressor.Filter(pair.Id, SyncSide.Remote, remote);

        if (pair.Direction == SyncDirection.TwoWay)
        {
            local = await HashLocalMoveCandidatesAsync(pair, mapper, local, cancellationToken);
        }
        else if (pair.Direction == SyncDirection.LocalToRemote)
        {
            local = await HashAmbiguousUploadCandidatesAsync(mapper, local, remote, cancellationToken);
        }

        return (local, remote, mapper);
    }

    /// <summary>
    /// Fills in <see cref="NodeFingerprint.ContentHash"/> for the local files that could be the
    /// destination of a move, so the pure reconciler can match them against the baseline's recorded
    /// hash (§11.3 / backlog B4). <see cref="LocalScanner"/> is stat-only by design and the reconciler
    /// does no IO, so this is the only place the hashes can come from.
    ///
    /// Narrowed twice, because hashing is the one genuinely expensive thing here:
    /// <list type="bullet">
    /// <item>Only files that are new since the baseline — an existing path can't be a move's target.</item>
    /// <item>Only those whose <b>size</b> matches something that disappeared from the baseline. A move
    /// preserves size exactly, so a size that matches nothing cannot be a move, and this alone turns
    /// "hash everything new" into "hash the handful that could possibly match".</item>
    /// </list>
    /// A first sync hashes nothing at all: with an empty baseline nothing has disappeared.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, NodeFingerprint>> HashLocalMoveCandidatesAsync(
        SyncPair pair, PathMapper mapper, IReadOnlyDictionary<string, NodeFingerprint> local, CancellationToken cancellationToken)
    {
        var baseline = await _stateStore.GetBaselineAsync(pair.Id, cancellationToken);

        var vanishedSizes = baseline
            .Where(entry => !entry.Value.IsFolder
                            && !local.ContainsKey(entry.Key)
                            && entry.Value.LocalAtSync?.ContentHash is not null
                            && entry.Value.LocalAtSync.Size is not null)
            .Select(entry => entry.Value.LocalAtSync!.Size!.Value)
            .ToHashSet();

        if (vanishedSizes.Count == 0)
        {
            return local;
        }

        var candidates = local
            .Where(entry => !entry.Value.IsFolder
                            && entry.Value.Size is { } size
                            && vanishedSizes.Contains(size)
                            && !baseline.ContainsKey(entry.Key))
            .Select(entry => entry.Key)
            .ToList();

        if (candidates.Count == 0)
        {
            return local;
        }

        var hashed = new Dictionary<string, NodeFingerprint>(local, StringComparer.Ordinal);
        foreach (var relativePath in candidates)
        {
            try
            {
                var hash = await _hasher.ComputeAsync(mapper.ToLocalAbsolute(relativePath), cancellationToken);
                hashed[relativePath] = hashed[relativePath] with { ContentHash = hash, HashAlgorithm = _hasher.Algorithm };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable right now: leave it hashless, which simply means no move is detected for
                // it and the ordinary upload path handles it.
            }
        }

        return hashed;
    }

    /// <summary>
    /// Fills in <see cref="NodeFingerprint.ContentHash"/> for local files whose size matches the
    /// remote copy at the same path but whose modified time doesn't (within
    /// <see cref="SyncReconciler.DefaultMtimeTolerance"/>) — the one case <c>SyncReconciler</c>'s
    /// own equivalence check can't resolve on its own for a one-way <c>LocalToRemote</c> pair,
    /// since <see cref="LocalScanner"/> never computes a hash and the remote's own timestamp isn't
    /// guaranteed to reflect the local file's actual mtime.
    ///
    /// This matters most for exactly the case that motivated it: a file that already existed
    /// independently on both sides before this pair was created (its local and remote copies were
    /// never related by an upload this app did), so their timestamps have nothing to do with each
    /// other and will essentially never agree — without this, such a file looks "changed" forever
    /// and gets re-uploaded on every single cycle, never converging. The same is true for any
    /// provider whose upload path doesn't preserve the source's claimed modification time.
    ///
    /// Narrowed to size-matching, mtime-ambiguous pairs for the same reason
    /// <see cref="HashLocalMoveCandidatesAsync"/> narrows its own candidate set: hashing is the one
    /// genuinely expensive thing here, and a file whose mtime already agrees needs no help.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, NodeFingerprint>> HashAmbiguousUploadCandidatesAsync(
        PathMapper mapper, IReadOnlyDictionary<string, NodeFingerprint> local, IReadOnlyDictionary<string, NodeFingerprint> remote, CancellationToken cancellationToken)
    {
        var candidates = local
            .Where(entry => !entry.Value.IsFolder
                            && remote.TryGetValue(entry.Key, out var r)
                            && !r.IsFolder
                            && r.ContentHash is not null
                            && r.HashAlgorithm == _hasher.Algorithm
                            && entry.Value.Size == r.Size
                            && !MtimesAgree(entry.Value.ModifiedAt, r.ModifiedAt))
            .Select(entry => entry.Key)
            .ToList();

        if (candidates.Count == 0)
        {
            return local;
        }

        var hashed = new Dictionary<string, NodeFingerprint>(local, StringComparer.Ordinal);
        foreach (var relativePath in candidates)
        {
            try
            {
                var hash = await _hasher.ComputeAsync(mapper.ToLocalAbsolute(relativePath), cancellationToken);
                hashed[relativePath] = hashed[relativePath] with { ContentHash = hash, HashAlgorithm = _hasher.Algorithm };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable right now: leave it hashless, which simply means the mtime mismatch
                // stands and the ordinary "upload it" path handles it — the same safe default as
                // before this method existed.
            }
        }

        return hashed;
    }

    private static bool MtimesAgree(DateTimeOffset? a, DateTimeOffset? b)
        => a is not null && b is not null && (a.Value - b.Value).Duration() <= SyncReconciler.DefaultMtimeTolerance;

    private async Task ExecuteOneAsync(RunContext context, QueuedSyncAction action, CancellationToken cancellationToken)
    {
        var isFolder = ResolveIsFolder(context, action);

        switch (action.Operation)
        {
            case SyncOperation.CreateLocalFolder:
                Directory.CreateDirectory(context.Mapper.ToLocalAbsolute(action.RelativePath));
                _echoSuppressor.SuppressWrite(context.Pair.Id, SyncSide.Local, action.RelativePath);
                break;

            case SyncOperation.CreateRemoteFolder:
                await CreateRemoteFolderAsync(context, action.RelativePath, cancellationToken);
                break;

            case SyncOperation.DownloadFile:
                await DownloadFileAsync(context, action.RelativePath, cancellationToken);
                break;

            case SyncOperation.UploadFile:
                await UploadFileAsync(context, action.RelativePath, cancellationToken);
                break;

            // Both deletions end with the node absent on *both* sides — that's the only shape the
            // decision table produces them in (§5.2: delete one side only when the other side is
            // already gone and the survivor is unmodified). So the baseline effect is known
            // without asking anyone: drop the row.
            //
            // Deliberately not re-reading the remote side the way every other operation does.
            // Proton's listing is not read-your-writes consistent right after a `trash`: a fresh
            // `filesystem list` issued immediately afterwards still returns the trashed node a
            // good fraction of the time (reproduced ~1 run in 3). Re-reading therefore recorded a
            // baseline row claiming the remote copy was alive, moments after we trashed it. It
            // also saves a ~3.5s CLI call per deletion.
            case SyncOperation.DeleteLocal:
                MoveToLocalTrash(context.Pair, context.Mapper, action.RelativePath);
                _echoSuppressor.SuppressDeletion(context.Pair.Id, SyncSide.Local, action.RelativePath);
                await ClearBaselineAsync(context, action.RelativePath, cancellationToken);
                return;

            case SyncOperation.TrashRemote:
                await _operations.TrashItemAsync(context.Mapper.ToRemoteAbsolute(action.RelativePath), cancellationToken);
                context.Baseline?.InvalidateRemoteFolder(ParentOf(action.RelativePath));
                _echoSuppressor.SuppressDeletion(context.Pair.Id, SyncSide.Remote, action.RelativePath);
                await ClearBaselineAsync(context, action.RelativePath, cancellationToken);
                return;

            case SyncOperation.ResolveConflictKeepBoth:
                await ResolveConflictKeepBothAsync(context, action, cancellationToken);
                break;

            case SyncOperation.RenameLocal:
                await RenameLocalAsync(context, action, cancellationToken);
                return; // its own baseline bookkeeping — two paths change, not one

            case SyncOperation.RenameRemote:
                await RenameRemoteAsync(context, action, cancellationToken);
                return;

            case SyncOperation.UpdateBaselineOnly:
                break; // the baseline write below is the entire point of this operation

            case SyncOperation.ClearBaseline:
                await ClearBaselineAsync(context, action.RelativePath, cancellationToken);
                return; // no fingerprint to record — the row is gone on both sides

            default:
                // RenameLocal/RenameRemote are §11/F5 territory: the reconciler never emits them
                // yet (it has no rename detection), so reaching here means a plan was produced by
                // something newer than this executor. Fail loudly rather than silently skipping.
                throw new NotSupportedException($"{action.Operation} is not implemented yet (docs/PLAN-LOCAL-SYNC.md §11 / F5).");
        }

        if (context.Baseline is not null)
        {
            await context.Baseline.RecordAsync(action.RelativePath, isFolder, _timeProvider.GetUtcNow(), cancellationToken);
        }
    }

    private static Task ClearBaselineAsync(RunContext context, string relativePath, CancellationToken cancellationToken)
        => context.Baseline?.ClearAsync(relativePath, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// The baseline row's <c>IsFolder</c> has to be right even for operations that don't imply it
    /// — <see cref="SyncOperation.UpdateBaselineOnly"/> fires for folders that already match on
    /// both sides, and recording those as files would store a bogus (empty) fingerprint. The
    /// scans are the authority; the local disk is the last resort for a path neither scan saw.
    /// </summary>
    private static bool ResolveIsFolder(RunContext context, QueuedSyncAction action)
    {
        if (action.Operation is SyncOperation.CreateLocalFolder or SyncOperation.CreateRemoteFolder)
        {
            return true;
        }

        if (action.Operation is SyncOperation.DownloadFile or SyncOperation.UploadFile or SyncOperation.ResolveConflictKeepBoth)
        {
            return false;
        }

        if (context.Remote.TryGetValue(action.RelativePath, out var remote))
        {
            return remote.IsFolder;
        }

        return context.Local.TryGetValue(action.RelativePath, out var local)
            ? local.IsFolder
            : Directory.Exists(context.Mapper.ToLocalAbsolute(action.RelativePath));
    }

    private async Task CreateRemoteFolderAsync(RunContext context, string relativePath, CancellationToken cancellationToken)
    {
        var parent = ParentOf(relativePath);
        var name = NameOf(relativePath);

        try
        {
            await _operations.CreateFolderAsync(context.Mapper.ToRemoteAbsolute(parent), name, cancellationToken);
        }
        catch (DriveException ex) when (ex.Kind == DriveErrorKind.AlreadyExists)
        {
            // Idempotent by design: a retried run, or a folder created by another client between
            // the scan and now, is a success for our purposes, not a failure.
        }

        context.Baseline?.InvalidateRemoteFolder(parent);
    }

    /// <summary>
    /// Uploads with the `replace` conflict strategy: the plan already decided this local version
    /// wins (either the remote side is absent, or the decision table found only the local side
    /// changed), so letting the CLI create a second "keep both" copy would contradict the plan.
    /// True content conflicts never reach here — they go through
    /// <see cref="ResolveConflictKeepBothAsync"/> or the policy branches in the reconciler.
    /// </summary>
    private async Task UploadFileAsync(RunContext context, string relativePath, CancellationToken cancellationToken)
    {
        var parent = ParentOf(relativePath);
        var localAbsolutePath = context.Mapper.ToLocalAbsolute(relativePath);
        if (!File.Exists(localAbsolutePath))
        {
            throw new FileNotFoundException($"'{relativePath}' desapareció localmente antes de poder subirse.", localAbsolutePath);
        }

        await _operations.UploadFilesAsync([localAbsolutePath], context.Mapper.ToRemoteAbsolute(parent),
            UploadConflictStrategy.Replace, cancellationToken);
        context.Baseline?.InvalidateRemoteFolder(parent);
    }

    /// <summary>
    /// §5.6's KeepBoth: the local version is renamed aside, the remote version takes the original
    /// name, and the renamed copy is uploaded — so both survive and neither side loses content.
    /// Ordered local-rename-first deliberately: if the download fails afterward, the local
    /// version still exists under the conflict name, whereas downloading first would overwrite it.
    /// </summary>
    private async Task ResolveConflictKeepBothAsync(RunContext context, QueuedSyncAction action, CancellationToken cancellationToken)
    {
        if (action.SecondaryPath is null)
        {
            throw new InvalidOperationException($"Una resolución KeepBoth para '{action.RelativePath}' no tiene ruta de copia de conflicto.");
        }

        var originalLocalPath = context.Mapper.ToLocalAbsolute(action.RelativePath);
        var conflictLocalPath = context.Mapper.ToLocalAbsolute(action.SecondaryPath);

        if (File.Exists(originalLocalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(conflictLocalPath)!);
            _echoSuppressor.SuppressWrite(context.Pair.Id, SyncSide.Local, action.SecondaryPath);
            File.Move(originalLocalPath, conflictLocalPath, overwrite: false);
        }

        await DownloadFileAsync(context, action.RelativePath, cancellationToken);

        if (File.Exists(conflictLocalPath))
        {
            await UploadFileAsync(context, action.SecondaryPath, cancellationToken);
            if (context.Baseline is not null)
            {
                await context.Baseline.RecordAsync(action.SecondaryPath, isFolder: false, _timeProvider.GetUtcNow(), cancellationToken);
            }
        }

        await LogAsync(context, SyncLogLevel.Warning, action.RelativePath,
            $"Conflict kept both versions: the local copy is now '{action.SecondaryPath}'.", cancellationToken);
    }

    /// <summary>
    /// Mirrors a remote move by moving the local file, instead of downloading content that never
    /// changed (§11 / Appendix A #3). Unlike every other operation this rewrites *two* baseline
    /// paths, so it does its own bookkeeping: the old row is forgotten and a new one recorded.
    /// </summary>
    private async Task RenameLocalAsync(RunContext context, QueuedSyncAction action, CancellationToken cancellationToken)
    {
        if (action.SecondaryPath is null)
        {
            throw new InvalidOperationException($"Un renombrado local de '{action.RelativePath}' no tiene ruta de destino.");
        }

        var source = context.Mapper.ToLocalAbsolute(action.RelativePath);
        var destination = context.Mapper.ToLocalAbsolute(action.SecondaryPath);

        if (!File.Exists(source))
        {
            // It vanished between the scan and now. Failing would be wrong: the next cycle rescans
            // and will download it at the new path, which is the correct outcome anyway.
            throw new FileNotFoundException($"'{action.RelativePath}' desapareció localmente antes de poder moverse.", source);
        }

        if (File.Exists(destination))
        {
            throw new IOException($"No se mueve '{action.RelativePath}' encima del archivo existente '{action.SecondaryPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Both ends are our own doing, and registered before the move so the watcher can't beat us
        // to it (§9).
        _echoSuppressor.SuppressDeletion(context.Pair.Id, SyncSide.Local, action.RelativePath);
        _echoSuppressor.SuppressWrite(context.Pair.Id, SyncSide.Local, action.SecondaryPath);
        File.Move(source, destination);

        if (context.Baseline is not null)
        {
            await context.Baseline.ClearAsync(action.RelativePath, cancellationToken);
            await context.Baseline.RecordAsync(action.SecondaryPath, isFolder: false, _timeProvider.GetUtcNow(), cancellationToken);
        }

        await LogAsync(context, SyncLogLevel.Info, action.RelativePath,
            $"Moved locally to '{action.SecondaryPath}' to match Proton Drive, without re-downloading it.", cancellationToken);
    }

    /// <summary>
    /// Mirrors a local move on Proton Drive, instead of uploading the content again and trashing the
    /// old copy (§11 / backlog B4).
    ///
    /// Needs up to two CLI calls, because the two commands each hold one thing fixed:
    /// `filesystem move` keeps the node's name and changes its parent, `filesystem rename` keeps the
    /// parent and changes the name. So a move that also renames needs both — move first, then rename,
    /// so the node reaches its destination folder before it takes its final name. If the second call
    /// fails the node is left in the right folder under the old name; nothing is lost, and the next
    /// cycle's rescan plans from whatever it actually finds.
    /// </summary>
    private async Task RenameRemoteAsync(RunContext context, QueuedSyncAction action, CancellationToken cancellationToken)
    {
        if (action.SecondaryPath is null)
        {
            throw new InvalidOperationException($"Un movimiento remoto de '{action.RelativePath}' no tiene ruta de destino.");
        }

        var oldParent = ParentOf(action.RelativePath);
        var newParent = ParentOf(action.SecondaryPath);
        var oldName = NameOf(action.RelativePath);
        var newName = NameOf(action.SecondaryPath);

        var currentRemotePath = context.Mapper.ToRemoteAbsolute(action.RelativePath);

        if (!string.Equals(oldParent, newParent, StringComparison.Ordinal))
        {
            await _operations.MoveItemAsync(currentRemotePath, context.Mapper.ToRemoteAbsolute(newParent), cancellationToken);
            // It now lives in the new folder, still under the old name.
            currentRemotePath = context.Mapper.ToRemoteAbsolute(newParent.Length == 0 ? oldName : $"{newParent}/{oldName}");
            context.Baseline?.InvalidateRemoteFolder(oldParent);
            context.Baseline?.InvalidateRemoteFolder(newParent);
        }

        if (!string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            await _operations.RenameItemAsync(currentRemotePath, newName, cancellationToken);
            context.Baseline?.InvalidateRemoteFolder(newParent);
        }

        // The old remote path is gone. Suppressed for the same reason a trash is (Appendix A #15): a
        // stale listing still reporting it, with the baseline row already moved, reads as "new
        // remotely" — and would download the file back under its old name.
        _echoSuppressor.SuppressDeletion(context.Pair.Id, SyncSide.Remote, action.RelativePath);

        if (context.Baseline is not null)
        {
            await context.Baseline.ClearAsync(action.RelativePath, cancellationToken);
            await context.Baseline.RecordAsync(action.SecondaryPath, isFolder: false, _timeProvider.GetUtcNow(), cancellationToken);
        }

        await LogAsync(context, SyncLogLevel.Info, action.RelativePath,
            $"Moved on Proton Drive to '{action.SecondaryPath}' to match this machine, without re-uploading it.", cancellationToken);
    }

    /// <summary>
    /// Downloads to a per-operation temp directory, then moves the result into place — per
    /// docs/PLAN-LOCAL-SYNC.md §7, so a crash mid-download never leaves a partial file under
    /// the real name. Explicitly sets the local mtime afterward: Appendix A #6 confirmed
    /// `filesystem download` does not preserve it.
    /// </summary>
    private async Task DownloadFileAsync(RunContext context, string relativePath, CancellationToken cancellationToken)
    {
        var localAbsolutePath = context.Mapper.ToLocalAbsolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localAbsolutePath)!);

        // Registered *before* the write, not after: the watcher can deliver the event while the
        // File.Move below is still in flight, and a suppression that arrives second is no
        // suppression at all (§9's infinite-loop bug).
        _echoSuppressor.SuppressWrite(context.Pair.Id, SyncSide.Local, relativePath);

        var tempDirectory = Path.Combine(context.Pair.LocalPath, SyncCrashRecovery.TempFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            await _operations.DownloadFileAsync(context.Mapper.ToRemoteAbsolute(relativePath), tempDirectory, cancellationToken);

            var fileName = Path.GetFileName(localAbsolutePath);
            var downloadedPath = Path.Combine(tempDirectory, fileName);
            if (!File.Exists(downloadedPath))
            {
                throw new IOException($"Se esperaba que la CLI descargara '{fileName}' en la carpeta temporal, pero no estaba ahí.");
            }

            File.Move(downloadedPath, localAbsolutePath, overwrite: true);

            if (context.Remote.TryGetValue(relativePath, out var fingerprint) && fingerprint.ModifiedAt is { } modifiedAt)
            {
                File.SetLastWriteTimeUtc(localAbsolutePath, modifiedAt.UtcDateTime);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);

                // Also remove the shared temp parent once it's empty, so a sync run doesn't leave
                // a visible (if dotfile-hidden) empty folder behind.
                var tempRoot = Path.GetDirectoryName(tempDirectory)!;
                if (Directory.Exists(tempRoot) && !Directory.EnumerateFileSystemEntries(tempRoot).Any())
                {
                    Directory.Delete(tempRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a leftover empty-ish temp folder isn't worth failing the sync over.
            }
        }
    }

    /// <summary>
    /// Never permanently deletes — moves into a dated trash folder under the pair's local
    /// root, per docs/PLAN-LOCAL-SYNC.md §11's cross-cutting safety rule.
    /// </summary>
    private static void MoveToLocalTrash(SyncPair pair, PathMapper mapper, string relativePath)
    {
        var sourcePath = mapper.ToLocalAbsolute(relativePath);
        var isDirectory = Directory.Exists(sourcePath);
        if (!isDirectory && !File.Exists(sourcePath))
        {
            return; // already gone — nothing to do
        }

        var trashRoot = Path.Combine(pair.LocalPath, ".mypersonaldrive-trash", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var trashPath = Path.Combine(trashRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);

        // A second delete of the same relative path on the same day would collide with a
        // previous trash entry; disambiguate rather than throwing or overwriting the earlier one.
        if (File.Exists(trashPath) || Directory.Exists(trashPath))
        {
            trashPath = $"{trashPath}.{Guid.NewGuid():N}";
        }

        if (isDirectory)
        {
            Directory.Move(sourcePath, trashPath);
        }
        else
        {
            File.Move(sourcePath, trashPath);
        }
    }

    private static string ParentOf(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : relativePath[..lastSlash];
    }

    private static string NameOf(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? relativePath : relativePath[(lastSlash + 1)..];
    }

    /// <summary>Everything one run needs to carry between actions.</summary>
    private sealed record RunContext(
        SyncPair Pair,
        PathMapper Mapper,
        IReadOnlyDictionary<string, NodeFingerprint> Local,
        IReadOnlyDictionary<string, NodeFingerprint> Remote,
        SyncBaselineWriter? Baseline);
}
