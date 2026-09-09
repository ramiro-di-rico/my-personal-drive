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
    LocalDeletedRemoteChanged,

    /// <summary>
    /// A one-way pair sharing its local folder found a destination file it did not write itself,
    /// differing from the source. Only raised for a pair with
    /// <see cref="SyncPair.SharesLocalFolder"/>: overwriting here is how two providers mirroring
    /// into one folder would clobber each other's same-named files on every run. See
    /// docs/PLAN-LOCAL-SYNC.md §12.
    /// </summary>
    ForeignDestinationFile
}
