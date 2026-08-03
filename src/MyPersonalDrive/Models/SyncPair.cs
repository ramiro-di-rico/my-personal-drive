namespace MyPersonalDrive.Models;

/// <summary>A configured `{ remote folder ↔ local folder }` sync pair. See docs/PLAN-LOCAL-SYNC.md §3.</summary>
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
    string? LastError);
