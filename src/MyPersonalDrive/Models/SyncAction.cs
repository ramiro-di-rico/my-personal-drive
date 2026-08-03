namespace MyPersonalDrive.Models;

/// <summary>
/// One step of a <see cref="SyncPlan"/>. <see cref="SecondaryPath"/> is used by rename/move
/// operations (the destination path) and by <see cref="SyncOperation.ResolveConflictKeepBoth"/>
/// (the renamed conflict-copy's relative path). <see cref="Priority"/> encodes the execution
/// band from docs/PLAN-LOCAL-SYNC.md §5.3 (lower runs first); the reconciler assigns it so the
/// plan comes out pre-sorted.
/// </summary>
public sealed record SyncAction(
    SyncOperation Operation,
    string RelativePath,
    string? SecondaryPath,
    long? Bytes,
    int Priority);
