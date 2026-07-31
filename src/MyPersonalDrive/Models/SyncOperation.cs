namespace MyPersonalDrive.Models;

public enum SyncOperation
{
    DownloadFile,
    UploadFile,
    CreateLocalFolder,
    CreateRemoteFolder,
    DeleteLocal,
    TrashRemote,
    RenameLocal,
    RenameRemote,
    UpdateBaselineOnly,
    ResolveConflictKeepBoth,

    /// <summary>
    /// Both sides deleted the node since the last successful sync — nothing to transfer, but
    /// the now-stale SyncState row for this path must be removed. Not in the original
    /// docs/PLAN-LOCAL-SYNC.md §3.2 enum; added during implementation because "both deleted"
    /// (§5.2's last decision-table row) needs a distinct effect from UpdateBaselineOnly
    /// (record current state) — this one means "forget this path entirely."
    /// </summary>
    ClearBaseline
}
