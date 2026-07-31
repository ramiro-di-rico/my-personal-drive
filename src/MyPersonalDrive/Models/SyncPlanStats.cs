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
    long BytesToUpload);
