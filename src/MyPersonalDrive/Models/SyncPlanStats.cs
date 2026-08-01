namespace MyPersonalDrive.Models;

/// <summary>Summary counts for the dry-run preview UI (docs/PLAN-LOCAL-SYNC.md §12).</summary>
public sealed record SyncPlanStats(
    int FilesToDownload,
    int FilesToUpload,
    int FoldersToCreateLocally,
    int FoldersToCreateRemotely,
    int ToDeleteLocal,
    int ToTrashRemote,
    int Conflicts,
    long BytesToDownload,
    long BytesToUpload,
    /// <summary>
    /// Files being moved rather than transferred, because the other side moved them and the content
    /// is unchanged (§11). Counted separately precisely because these cost no bytes — folding them
    /// into the download count would misreport the work, and omitting them entirely would make a
    /// plan that only moves files look like it does nothing.
    /// </summary>
    int FilesToMoveLocally = 0);
