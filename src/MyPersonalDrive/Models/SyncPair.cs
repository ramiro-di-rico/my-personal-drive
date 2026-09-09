namespace MyPersonalDrive.Models;

/// <summary>A configured `{ remote folder ↔ local folder }` sync pair. See docs/PLAN-LOCAL-SYNC.md §3.</summary>
/// <param name="MirrorDeletes">
/// Only consulted for a one-way pair (<see cref="SyncDirection.RemoteToLocal"/>/
/// <see cref="SyncDirection.LocalToRemote"/>; ignored for <see cref="SyncDirection.TwoWay"/>,
/// which already tracks deletions through its baseline). True (the default, and today's only
/// behavior before this field existed) mirrors the source side exactly — an item missing from
/// the source gets deleted from the destination. False makes the pair additive: the destination
/// keeps whatever it already had, and the sync only creates/updates.
/// </param>
/// <param name="SharesLocalFolder">
/// True when another pair — possibly on another account, i.e. another provider — is configured
/// against this exact same local folder. Maintained by <c>ViewModels.Sync.SyncPanelViewModel</c>
/// whenever pairs are added or removed, not derived at sync time: <c>Services.Sync.SyncExecutor</c>
/// only ever sees one account's store, and a shared folder is shared across accounts by definition.
///
/// It changes two things, and only for the pairs it is set on, so a pair that owns its folder
/// alone behaves exactly as it did before this flag existed:
/// <list type="bullet">
/// <item>the pair keeps a baseline even when one-way, which it otherwise would not
/// (<c>SyncExecutor.LoadBaselineAsync</c>) — without one it cannot tell a file it downloaded
/// itself from one the other provider's pair put there;</item>
/// <item><c>SyncReconciler</c> refuses to overwrite a destination file this pair did not write,
/// raising <see cref="ConflictReason.ForeignDestinationFile"/> instead. Two pairs mirroring
/// different providers into one folder would otherwise overwrite each other's copy of any
/// same-named file on every run, forever.</item>
/// </list>
/// See docs/PLAN-LOCAL-SYNC.md §12.
/// </param>
public sealed record SyncPair(
    int Id,
    string RemotePath,
    string LocalPath,
    SyncDirection Direction,
    ConflictPolicy ConflictPolicy,
    bool IsEnabled,
    bool IsPaused,
    IReadOnlyList<string> ExcludeGlobs,
    DateTimeOffset? LastSyncAt,
    SyncPairStatus LastStatus,
    string? LastError,
    bool MirrorDeletes = true,
    bool SharesLocalFolder = false);
