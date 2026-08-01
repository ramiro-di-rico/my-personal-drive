using MyPersonalDrive.Models;

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

    private readonly ProtonDriveService _protonDriveService;
    private readonly SyncStateStore _stateStore;
    private readonly ILocalScanner _localScanner;
    private readonly IRemoteScanner _remoteScanner;
    private readonly TimeProvider _timeProvider;
    private readonly SyncEchoSuppressor _echoSuppressor;

    public SyncExecutor(
        ProtonDriveService protonDriveService,
        SyncStateStore stateStore,
        ILocalScanner localScanner,
        IRemoteScanner remoteScanner,
        TimeProvider? timeProvider = null,
        SyncEchoSuppressor? echoSuppressor = null)
    {
        _protonDriveService = protonDriveService;
        _stateStore = stateStore;
        _localScanner = localScanner;
        _remoteScanner = remoteScanner;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Shared across runs by design: the whole point is to remember a deletion from the run
        // that performed it into the run that scans afterwards.
        _echoSuppressor = echoSuppressor ?? new SyncEchoSuppressor(_timeProvider);
    }

    /// <summary>
    /// Scans and reconciles without touching anything — the dry-run preview from
    /// docs/PLAN-LOCAL-SYNC.md §12, meant to be shown to the user before the first sync of a
    /// pair (and on request afterward).
    /// </summary>
    public async Task<SyncPlan> PreviewAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        var (local, remote, _) = await ScanBothSidesAsync(pair, cancellationToken);
        return SyncReconciler.Reconcile(pair.Id, pair.Direction, pair.ConflictPolicy, local, remote,
            await LoadBaselineAsync(pair, cancellationToken), _timeProvider.GetUtcNow());
    }

    /// <summary>Scans, reconciles, enqueues the plan durably, then executes it.</summary>
    public async Task<SyncPlan> RunAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        var (local, remote, mapper) = await ScanBothSidesAsync(pair, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var plan = SyncReconciler.Reconcile(pair.Id, pair.Direction, pair.ConflictPolicy, local, remote,
            await LoadBaselineAsync(pair, cancellationToken), now);

        // Clear yesterday's completed rows before adding today's. Cheap, and it keeps the queue's
        // size proportional to outstanding work rather than to how long automatic sync has been on.
        await _stateStore.PruneCompletedAsync(now - CompletedRetention, cancellationToken);

        await _stateStore.EnqueueActionsAsync(pair.Id, plan.Actions, now, cancellationToken);

        // Conflicts the reconciler left unresolved (the `Ask` policy) become durable 'Conflict'
        // rows rather than being dropped on the floor — §5.6. Stale ones are cleared first: a
        // difference resolved by any means at all (the panel, an edit by hand, another client) must
        // stop being reported, and nothing else ever removes a Conflict row.
        var unresolved = pair.ConflictPolicy == ConflictPolicy.Ask ? plan.Conflicts : [];
        await _stateStore.ClearStaleConflictsAsync(pair.Id, unresolved.Select(c => c.RelativePath).ToList(), cancellationToken);
        await _stateStore.EnqueueConflictsAsync(pair.Id, unresolved, now, cancellationToken);

        var context = new RunContext(pair, mapper, local, remote,
            pair.Direction == SyncDirection.TwoWay ? new SyncBaselineWriter(_protonDriveService, _stateStore, mapper, pair.Id) : null);
        context.Baseline?.SeedFromScan(remote);

        var (failureCount, aborted) = await DrainQueueAsync(context, cancellationToken);

        var status = (failureCount, unresolved.Count) switch
        {
            (0, 0) => SyncPairStatus.Ok,
            _ => SyncPairStatus.PartialFailure,
        };
        var error = BuildStatusMessage(failureCount, unresolved.Count, aborted);
        await _stateStore.UpdatePairStatusAsync(pair.Id, _timeProvider.GetUtcNow(), status, error, cancellationToken);

        return plan;
    }

    private static string? BuildStatusMessage(int failureCount, int conflictCount, bool aborted)
    {
        var parts = new List<string>();
        if (failureCount > 0)
        {
            parts.Add($"{failureCount} action(s) failed");
        }

        if (conflictCount > 0)
        {
            parts.Add($"{conflictCount} conflict(s) awaiting your decision");
        }

        if (aborted)
        {
            parts.Add("run stopped early (sign in again, or free up space, then retry)");
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
        foreach (var queuedAction in await _stateStore.GetPendingActionsAsync(context.Pair.Id, _timeProvider.GetUtcNow(), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _stateStore.MarkRunningAsync(queuedAction.Id, cancellationToken);

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
        }

        return (failureCount, false);
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
            foreach (var item in await _protonDriveService.LoadFolderAsync(mapper.ToRemoteAbsolute(parent), cancellationToken))
            {
                var relativePath = parent.Length == 0 ? item.Name : $"{parent}/{item.Name}";
                remote[relativePath] = new NodeFingerprint(relativePath, item.IsFolder, item.Size, item.ModifiedAt, item.NodeId, item.ContentHash);
            }
        }
        catch (CliException) when (resolution == ConflictResolution.KeepLocal)
        {
            // Keeping the local version doesn't need to know anything about the remote one.
        }

        var context = new RunContext(pair, mapper, new Dictionary<string, NodeFingerprint>(), remote,
            pair.Direction == SyncDirection.TwoWay ? new SyncBaselineWriter(_protonDriveService, _stateStore, mapper, pair.Id) : null);
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

    private async Task<(IReadOnlyDictionary<string, NodeFingerprint> Local, IReadOnlyDictionary<string, NodeFingerprint> Remote, PathMapper Mapper)> ScanBothSidesAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        var exclusions = new ExclusionMatcher(pair.ExcludeGlobs);

        Directory.CreateDirectory(pair.LocalPath);
        var local = await _localScanner.ScanAsync(pair.LocalPath, exclusions, cancellationToken);
        var remote = await _remoteScanner.ScanAsync(pair.RemotePath, mapper, exclusions, cancellationToken);

        // Applied to both sides' scans, and to the preview as much as to the run — otherwise the
        // dry-run would offer to download a file this engine just deleted (Appendix A #15).
        local = _echoSuppressor.Filter(pair.Id, SyncSide.Local, local);
        remote = _echoSuppressor.Filter(pair.Id, SyncSide.Remote, remote);

        return (local, remote, mapper);
    }

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
                await _protonDriveService.TrashItemAsync(context.Mapper.ToRemoteAbsolute(action.RelativePath), cancellationToken);
                context.Baseline?.InvalidateRemoteFolder(ParentOf(action.RelativePath));
                _echoSuppressor.SuppressDeletion(context.Pair.Id, SyncSide.Remote, action.RelativePath);
                await ClearBaselineAsync(context, action.RelativePath, cancellationToken);
                return;

            case SyncOperation.ResolveConflictKeepBoth:
                await ResolveConflictKeepBothAsync(context, action, cancellationToken);
                break;

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
            await _protonDriveService.CreateFolderAsync(context.Mapper.ToRemoteAbsolute(parent), name, cancellationToken);
        }
        catch (CliException ex) when (ex.Kind == CliErrorKind.AlreadyExists)
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
            throw new FileNotFoundException($"'{relativePath}' disappeared locally before it could be uploaded.", localAbsolutePath);
        }

        await _protonDriveService.UploadFilesAsync([localAbsolutePath], context.Mapper.ToRemoteAbsolute(parent),
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
            throw new InvalidOperationException($"A KeepBoth resolution for '{action.RelativePath}' has no conflict-copy path.");
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
            await _protonDriveService.DownloadFileAsync(context.Mapper.ToRemoteAbsolute(relativePath), tempDirectory, cancellationToken);

            var fileName = Path.GetFileName(localAbsolutePath);
            var downloadedPath = Path.Combine(tempDirectory, fileName);
            if (!File.Exists(downloadedPath))
            {
                throw new IOException($"Expected the CLI to download '{fileName}' into the temp folder, but it wasn't there.");
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

        var trashRoot = Path.Combine(pair.LocalPath, ".mypersonaldrive-trash", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
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
