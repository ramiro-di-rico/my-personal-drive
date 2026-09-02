namespace MyPersonalDrive.Models;

/// <summary>
/// What the "add pair" dialog collected. A record rather than a tuple because F2 made this grow
/// past the two paths it started as (direction and conflict policy are now the user's choice),
/// and every future field from docs/PLAN-LOCAL-SYNC.md §12 — exclusion globs, for one — lands here.
/// </summary>
public sealed record NewSyncPairRequest(
    string RemotePath,
    string LocalPath,
    SyncDirection Direction,
    ConflictPolicy ConflictPolicy,
    bool MirrorDeletes = true);
