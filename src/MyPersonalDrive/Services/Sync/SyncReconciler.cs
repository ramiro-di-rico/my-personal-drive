using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Pure reconciliation engine: three fingerprint maps in, a <see cref="SyncPlan"/> out. No IO,
/// no wall-clock reads (the caller supplies <c>conflictTimestamp</c> so this stays
/// deterministic and unit-testable without a CLI or a disk). Implements the decision table in
/// docs/PLAN-LOCAL-SYNC.md §5.2, updated per Appendix A's F0 findings: change detection is
/// hash-first (§5.4) and folders are never diffed by content (only by presence/absence) since
/// they carry no hash and their mtime jitters trivially whenever a child changes.
/// </summary>
public static class SyncReconciler
{
    public static readonly TimeSpan DefaultMtimeTolerance = TimeSpan.FromSeconds(2);

    // Execution bands from §5.3, spaced widely enough that per-item depth adjustments (used to
    // order creates shallow-first and deletes deep-first) never spill into a neighboring band
    // for any realistically deep folder tree.
    private const int BandCreate = 0;
    private const int BandTransferOrRename = 1000;
    private const int BandDelete = 2000;
    private const int BandBaseline = 3000;

    public static SyncPlan Reconcile(
        int pairId,
        SyncDirection direction,
        ConflictPolicy conflictPolicy,
        IReadOnlyDictionary<string, NodeFingerprint> local,
        IReadOnlyDictionary<string, NodeFingerprint> remote,
        IReadOnlyDictionary<string, SyncBaselineEntry> baseline,
        DateTimeOffset conflictTimestamp,
        TimeSpan? mtimeTolerance = null)
    {
        var tolerance = mtimeTolerance ?? DefaultMtimeTolerance;
        var actions = new List<SyncAction>();
        var conflicts = new List<SyncConflict>();

        var allPaths = new SortedSet<string>(StringComparer.Ordinal);
        allPaths.UnionWith(local.Keys);
        allPaths.UnionWith(remote.Keys);
        allPaths.UnionWith(baseline.Keys);

        foreach (var path in allPaths)
        {
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            baseline.TryGetValue(path, out var b);
            var isFolder = l?.IsFolder ?? r?.IsFolder ?? b?.IsFolder ?? false;

            switch (direction)
            {
                case SyncDirection.RemoteToLocal:
                    ReconcileOneWay(path, isFolder, source: r, destination: l, tolerance,
                        createDestinationFolder: SyncOperation.CreateLocalFolder,
                        transferToDestination: SyncOperation.DownloadFile,
                        deleteDestination: SyncOperation.DeleteLocal,
                        actions);
                    break;

                case SyncDirection.LocalToRemote:
                    ReconcileOneWay(path, isFolder, source: l, destination: r, tolerance,
                        createDestinationFolder: SyncOperation.CreateRemoteFolder,
                        transferToDestination: SyncOperation.UploadFile,
                        deleteDestination: SyncOperation.TrashRemote,
                        actions);
                    break;

                default:
                    ReconcileTwoWay(path, isFolder, l, r, b, tolerance, conflictPolicy, conflictTimestamp, actions, conflicts);
                    break;
            }
        }

        var ordered = actions.OrderBy(a => a.Priority).ThenBy(a => a.RelativePath, StringComparer.Ordinal).ToList();
        return new SyncPlan(pairId, ordered, conflicts, ComputeStats(ordered, conflicts));
    }

    /// <summary>
    /// RemoteToLocal / LocalToRemote: the source side is always authoritative. No baseline is
    /// consulted — a one-way mirror doesn't need one, and diverging destination-side changes
    /// are simply overwritten (per §5.2's note on one-way modes), which is exactly why F1
    /// starts with RemoteToLocal: it's the direction that can't destroy cloud data.
    /// </summary>
    private static void ReconcileOneWay(
        string path, bool isFolder, NodeFingerprint? source, NodeFingerprint? destination, TimeSpan tolerance,
        SyncOperation createDestinationFolder, SyncOperation transferToDestination, SyncOperation deleteDestination,
        List<SyncAction> actions)
    {
        if (source is null)
        {
            if (destination is not null)
            {
                actions.Add(new SyncAction(deleteDestination, path, null, null, PriorityFor(deleteDestination, path)));
            }

            return;
        }

        if (isFolder)
        {
            if (destination is null)
            {
                actions.Add(new SyncAction(createDestinationFolder, path, null, null, PriorityFor(createDestinationFolder, path)));
            }

            return;
        }

        if (destination is null || IsChanged(source, destination, tolerance))
        {
            actions.Add(new SyncAction(transferToDestination, path, null, source.Size, PriorityFor(transferToDestination, path)));
        }
    }

    private static void ReconcileTwoWay(
        string path, bool isFolder, NodeFingerprint? l, NodeFingerprint? r, SyncBaselineEntry? b,
        TimeSpan tolerance, ConflictPolicy policy, DateTimeOffset conflictTimestamp,
        List<SyncAction> actions, List<SyncConflict> conflicts)
    {
        var inL = l is not null;
        var inR = r is not null;
        var inB = b is not null;

        if (inL && inR && !inB)
        {
            if (!isFolder && IsChanged(l, r, tolerance))
            {
                ResolveConflict(path, isFolder, l, r, policy, ConflictReason.BothAppearedDiffering, conflictTimestamp, actions, conflicts);
            }
            else
            {
                actions.Add(UpdateBaselineOnly(path));
            }

            return;
        }

        if (inL && !inR && !inB)
        {
            var op = isFolder ? SyncOperation.CreateRemoteFolder : SyncOperation.UploadFile;
            actions.Add(new SyncAction(op, path, null, l!.Size, PriorityFor(op, path)));
            return;
        }

        if (!inL && inR && !inB)
        {
            var op = isFolder ? SyncOperation.CreateLocalFolder : SyncOperation.DownloadFile;
            actions.Add(new SyncAction(op, path, null, r!.Size, PriorityFor(op, path)));
            return;
        }

        if (inL && inR && inB)
        {
            var lChanged = !isFolder && IsChanged(l, b!.LocalAtSync, tolerance);
            var rChanged = !isFolder && IsChanged(r, b!.RemoteAtSync, tolerance);

            if (!lChanged && !rChanged)
            {
                return;
            }

            if (lChanged && !rChanged)
            {
                actions.Add(new SyncAction(SyncOperation.UploadFile, path, null, l!.Size, PriorityFor(SyncOperation.UploadFile, path)));
                return;
            }

            if (!lChanged)
            {
                actions.Add(new SyncAction(SyncOperation.DownloadFile, path, null, r!.Size, PriorityFor(SyncOperation.DownloadFile, path)));
                return;
            }

            ResolveConflict(path, isFolder, l, r, policy, ConflictReason.BothChanged, conflictTimestamp, actions, conflicts);
            return;
        }

        if (inL && !inR && inB)
        {
            var lChanged = !isFolder && IsChanged(l, b!.LocalAtSync, tolerance);
            if (!lChanged)
            {
                actions.Add(new SyncAction(SyncOperation.DeleteLocal, path, null, null, PriorityFor(SyncOperation.DeleteLocal, path)));
            }
            else
            {
                // Auto-resolved, not policy-gated: recreating it remotely can't lose data (the
                // local copy is untouched either way). See docs/PLAN-LOCAL-SYNC.md §5.2.
                conflicts.Add(new SyncConflict(path, ConflictReason.RemoteDeletedLocalChanged));
                var op = isFolder ? SyncOperation.CreateRemoteFolder : SyncOperation.UploadFile;
                actions.Add(new SyncAction(op, path, null, l!.Size, PriorityFor(op, path)));
            }

            return;
        }

        if (!inL && inR && inB)
        {
            var rChanged = !isFolder && IsChanged(r, b!.RemoteAtSync, tolerance);
            if (!rChanged)
            {
                actions.Add(new SyncAction(SyncOperation.TrashRemote, path, null, null, PriorityFor(SyncOperation.TrashRemote, path)));
            }
            else
            {
                conflicts.Add(new SyncConflict(path, ConflictReason.LocalDeletedRemoteChanged));
                var op = isFolder ? SyncOperation.CreateLocalFolder : SyncOperation.DownloadFile;
                actions.Add(new SyncAction(op, path, null, r!.Size, PriorityFor(op, path)));
            }

            return;
        }

        if (!inL && !inR && inB)
        {
            actions.Add(new SyncAction(SyncOperation.ClearBaseline, path, null, null, PriorityFor(SyncOperation.ClearBaseline, path)));
        }
    }

    /// <summary>
    /// A real content conflict (both sides changed, or both appeared differing with no
    /// baseline to break the tie). Policy-gated, unlike the auto-resolved
    /// delete-vs-modify branches above.
    /// </summary>
    private static void ResolveConflict(
        string path, bool isFolder, NodeFingerprint? l, NodeFingerprint? r,
        ConflictPolicy policy, ConflictReason reason, DateTimeOffset conflictTimestamp,
        List<SyncAction> actions, List<SyncConflict> conflicts)
    {
        conflicts.Add(new SyncConflict(path, reason));

        switch (policy)
        {
            case ConflictPolicy.PreferLocal:
                var uploadOp = isFolder ? SyncOperation.CreateRemoteFolder : SyncOperation.UploadFile;
                actions.Add(new SyncAction(uploadOp, path, null, l?.Size, PriorityFor(uploadOp, path)));
                break;

            case ConflictPolicy.PreferRemote:
                var downloadOp = isFolder ? SyncOperation.CreateLocalFolder : SyncOperation.DownloadFile;
                actions.Add(new SyncAction(downloadOp, path, null, r?.Size, PriorityFor(downloadOp, path)));
                break;

            case ConflictPolicy.KeepBoth:
                var conflictCopyPath = BuildConflictCopyPath(path, conflictTimestamp);
                actions.Add(new SyncAction(SyncOperation.ResolveConflictKeepBoth, path, conflictCopyPath, l?.Size, PriorityFor(SyncOperation.ResolveConflictKeepBoth, path)));
                break;

            case ConflictPolicy.Ask:
            default:
                // No action yet: the conflict is recorded above; the UI resolves it later and
                // the executor marks the corresponding SyncQueue row 'Conflict' in the meantime.
                break;
        }
    }

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

    private static SyncAction UpdateBaselineOnly(string path)
        => new(SyncOperation.UpdateBaselineOnly, path, null, null, PriorityFor(SyncOperation.UpdateBaselineOnly, path));

    /// <summary>
    /// "Unchanged" per §5.4: hash+size equality when both sides have a hash (exact, no
    /// tolerance needed); otherwise size equality plus mtime within <paramref name="tolerance"/>.
    /// A missing fingerprint on either side counts as changed — there's nothing to compare.
    /// </summary>
    private static bool IsChanged(NodeFingerprint? current, NodeFingerprint? baseline, TimeSpan tolerance)
        => !AreEquivalent(current, baseline, tolerance);

    private static bool AreEquivalent(NodeFingerprint? a, NodeFingerprint? b, TimeSpan tolerance)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.Size != b.Size)
        {
            return false;
        }

        if (a.ContentHash is not null && b.ContentHash is not null)
        {
            return string.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal);
        }

        if (a.ModifiedAt is null || b.ModifiedAt is null)
        {
            return a.ModifiedAt == b.ModifiedAt;
        }

        return (a.ModifiedAt.Value - b.ModifiedAt.Value).Duration() <= tolerance;
    }

    private static int PriorityFor(SyncOperation operation, string relativePath)
    {
        var depth = relativePath.Count(c => c == '/');
        return operation switch
        {
            SyncOperation.CreateLocalFolder or SyncOperation.CreateRemoteFolder => BandCreate + depth,
            SyncOperation.DownloadFile or SyncOperation.UploadFile
                or SyncOperation.ResolveConflictKeepBoth
                or SyncOperation.RenameLocal or SyncOperation.RenameRemote => BandTransferOrRename,
            SyncOperation.DeleteLocal or SyncOperation.TrashRemote => Math.Max(BandTransferOrRename + 1, BandDelete - depth),
            _ => BandBaseline,
        };
    }

    private static SyncPlanStats ComputeStats(IReadOnlyList<SyncAction> actions, IReadOnlyList<SyncConflict> conflicts)
    {
        int filesToDownload = 0, filesToUpload = 0, foldersLocal = 0, foldersRemote = 0, deleteLocal = 0, trashRemote = 0;
        long bytesToDownload = 0, bytesToUpload = 0;

        foreach (var action in actions)
        {
            switch (action.Operation)
            {
                case SyncOperation.DownloadFile:
                    filesToDownload++;
                    bytesToDownload += action.Bytes ?? 0;
                    break;
                case SyncOperation.UploadFile:
                    filesToUpload++;
                    bytesToUpload += action.Bytes ?? 0;
                    break;
                case SyncOperation.CreateLocalFolder:
                    foldersLocal++;
                    break;
                case SyncOperation.CreateRemoteFolder:
                    foldersRemote++;
                    break;
                case SyncOperation.DeleteLocal:
                    deleteLocal++;
                    break;
                case SyncOperation.TrashRemote:
                    trashRemote++;
                    break;
                case SyncOperation.ResolveConflictKeepBoth:
                    filesToDownload++;
                    filesToUpload++;
                    bytesToDownload += action.Bytes ?? 0;
                    bytesToUpload += action.Bytes ?? 0;
                    break;
            }
        }

        return new SyncPlanStats(filesToDownload, filesToUpload, foldersLocal, foldersRemote, deleteLocal, trashRemote, conflicts.Count, bytesToDownload, bytesToUpload);
    }
}
