using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Scans both sides of a pair, reconciles, and executes the resulting plan against the local
/// filesystem and the CLI. Scoped to <see cref="SyncDirection.RemoteToLocal"/> only for now —
/// per docs/PLAN-LOCAL-SYNC.md §13 (F1), that's the direction that can't destroy cloud data,
/// so it's the one this milestone ships first. <see cref="RunAsync"/> throws
/// <see cref="NotSupportedException"/> for any other direction rather than silently doing the
/// wrong thing.
/// </summary>
public sealed class SyncExecutor
{
    private readonly ProtonDriveService _protonDriveService;
    private readonly SyncStateStore _stateStore;
    private readonly ILocalScanner _localScanner;
    private readonly IRemoteScanner _remoteScanner;

    public SyncExecutor(ProtonDriveService protonDriveService, SyncStateStore stateStore, ILocalScanner localScanner, IRemoteScanner remoteScanner)
    {
        _protonDriveService = protonDriveService;
        _stateStore = stateStore;
        _localScanner = localScanner;
        _remoteScanner = remoteScanner;
    }

    /// <summary>
    /// Scans and reconciles without touching anything — the dry-run preview from
    /// docs/PLAN-LOCAL-SYNC.md §12, meant to be shown to the user before the first sync of a
    /// pair (and on request afterward).
    /// </summary>
    public async Task<SyncPlan> PreviewAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        var (local, remote, _) = await ScanBothSidesAsync(pair, cancellationToken);
        var baseline = pair.Direction == SyncDirection.TwoWay
            ? await _stateStore.GetBaselineAsync(pair.Id, cancellationToken)
            : new Dictionary<string, SyncBaselineEntry>();

        return SyncReconciler.Reconcile(pair.Id, pair.Direction, pair.ConflictPolicy, local, remote, baseline, DateTimeOffset.UtcNow);
    }

    /// <summary>Scans, reconciles, enqueues the plan durably, then executes it.</summary>
    public async Task<SyncPlan> RunAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        if (pair.Direction != SyncDirection.RemoteToLocal)
        {
            throw new NotSupportedException(
                $"SyncExecutor only implements {nameof(SyncDirection.RemoteToLocal)} so far — " +
                $"{pair.Direction} needs the baseline-aware TwoWay path from docs/PLAN-LOCAL-SYNC.md F2.");
        }

        var (local, remote, mapper) = await ScanBothSidesAsync(pair, cancellationToken);
        var plan = SyncReconciler.Reconcile(pair.Id, pair.Direction, pair.ConflictPolicy, local, remote, new Dictionary<string, SyncBaselineEntry>(), DateTimeOffset.UtcNow);

        await _stateStore.EnqueueActionsAsync(pair.Id, plan.Actions, DateTimeOffset.UtcNow, cancellationToken);

        var failureCount = 0;
        foreach (var queuedAction in await _stateStore.GetPendingActionsAsync(pair.Id, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _stateStore.MarkRunningAsync(queuedAction.Id, cancellationToken);

            try
            {
                await ExecuteOneAsync(pair, mapper, queuedAction, remote, cancellationToken);
                await _stateStore.MarkDoneAsync(queuedAction.Id, DateTimeOffset.UtcNow, cancellationToken);
                await _stateStore.LogAsync(pair.Id, SyncLogLevel.Info, queuedAction.RelativePath, $"{queuedAction.Operation} completed.", DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failureCount++;
                await _stateStore.MarkFailedAsync(queuedAction.Id, ex.Message, nextAttemptAt: null, cancellationToken);
                await _stateStore.LogAsync(pair.Id, SyncLogLevel.Error, queuedAction.RelativePath, $"{queuedAction.Operation} failed: {ex.Message}", DateTimeOffset.UtcNow, cancellationToken);
            }
        }

        var status = failureCount == 0 ? SyncPairStatus.Ok : SyncPairStatus.PartialFailure;
        await _stateStore.UpdatePairStatusAsync(pair.Id, DateTimeOffset.UtcNow, status, failureCount == 0 ? null : $"{failureCount} action(s) failed", cancellationToken);

        return plan;
    }

    private async Task<(IReadOnlyDictionary<string, NodeFingerprint> Local, IReadOnlyDictionary<string, NodeFingerprint> Remote, PathMapper Mapper)> ScanBothSidesAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        var exclusions = new ExclusionMatcher(pair.ExcludeGlobs);

        Directory.CreateDirectory(pair.LocalPath);
        var local = await _localScanner.ScanAsync(pair.LocalPath, exclusions, cancellationToken);
        var remote = await _remoteScanner.ScanAsync(pair.RemotePath, mapper, exclusions, cancellationToken);

        return (local, remote, mapper);
    }

    private async Task ExecuteOneAsync(SyncPair pair, PathMapper mapper, QueuedSyncAction action, IReadOnlyDictionary<string, NodeFingerprint> remote, CancellationToken cancellationToken)
    {
        switch (action.Operation)
        {
            case SyncOperation.CreateLocalFolder:
                Directory.CreateDirectory(mapper.ToLocalAbsolute(action.RelativePath));
                break;

            case SyncOperation.DownloadFile:
                await DownloadFileAsync(pair, mapper, action, remote, cancellationToken);
                break;

            case SyncOperation.DeleteLocal:
                MoveToLocalTrash(pair, mapper, action.RelativePath);
                break;

            default:
                // TwoWay-only operations (UploadFile, rename/move, conflict resolution,
                // baseline bookkeeping) aren't reachable here — RemoteToLocal's reconciliation
                // never produces them (see SyncReconciler.ReconcileOneWay) — but guard anyway
                // rather than silently doing nothing if that ever changes.
                throw new NotSupportedException($"{action.Operation} is not implemented for RemoteToLocal sync.");
        }
    }

    /// <summary>
    /// Downloads to a per-operation temp directory, then moves the result into place — per
    /// docs/PLAN-LOCAL-SYNC.md §7, so a crash mid-download never leaves a partial file under
    /// the real name. Explicitly sets the local mtime afterward: Appendix A #6 confirmed
    /// `filesystem download` does not preserve it.
    /// </summary>
    private async Task DownloadFileAsync(SyncPair pair, PathMapper mapper, QueuedSyncAction action, IReadOnlyDictionary<string, NodeFingerprint> remote, CancellationToken cancellationToken)
    {
        var localAbsolutePath = mapper.ToLocalAbsolute(action.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localAbsolutePath)!);

        var tempDirectory = Path.Combine(pair.LocalPath, ".mypersonaldrive-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            await _protonDriveService.DownloadFileAsync(mapper.ToRemoteAbsolute(action.RelativePath), tempDirectory, cancellationToken);

            var fileName = Path.GetFileName(localAbsolutePath);
            var downloadedPath = Path.Combine(tempDirectory, fileName);
            if (!File.Exists(downloadedPath))
            {
                throw new IOException($"Expected the CLI to download '{fileName}' into the temp folder, but it wasn't there.");
            }

            File.Move(downloadedPath, localAbsolutePath, overwrite: true);

            if (remote.TryGetValue(action.RelativePath, out var fingerprint) && fingerprint.ModifiedAt is { } modifiedAt)
            {
                File.SetLastWriteTimeUtc(localAbsolutePath, modifiedAt.UtcDateTime);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);

                // Also remove the shared .mypersonaldrive-tmp parent once it's empty, so a
                // sync run doesn't leave a visible (if dotfile-hidden) empty folder behind.
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
}
