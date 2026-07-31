namespace MyPersonalDrive.Models;

public enum ConflictReason
{
    /// <summary>Both sides appeared with no baseline and differ — no way to tell which is "right."</summary>
    BothAppearedDiffering,

    /// <summary>Both sides changed since the last successful sync.</summary>
    BothChanged,

    /// <summary>Deleted remotely, but the local copy was also modified — auto-resolved by re-uploading.</summary>
    RemoteDeletedLocalChanged,

    /// <summary>Deleted locally, but the remote copy was also modified — auto-resolved by re-downloading.</summary>
    LocalDeletedRemoteChanged
}
