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
    bool MirrorDeletes = true);
