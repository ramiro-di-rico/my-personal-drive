using MyPersonalDrive.Models;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.ViewModels.Local;

/// <summary>
/// The context-menu callbacks a local row needs (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6) — the
/// local-pane counterpart to <see cref="DriveNodeSyncActions"/>. Every member is optional, so a row
/// built without this still works exactly like before this task (read-only browsing).
/// </summary>
public sealed class LocalNodeSyncActions
{
    /// <summary>Looks up the configured sync pair (if any) whose local side is this row's path.</summary>
    public Func<DriveItem, SyncPairViewModel?>? FindSyncPair { get; init; }

    public Func<DriveItem, Task>? SyncSelectedPathAsync { get; init; }

    public Func<DriveItem, Task>? CopyPathAsync { get; init; }

    public Func<DriveItem, Task>? RenameAsync { get; init; }

    public Func<DriveItem, Task>? DeleteAsync { get; init; }

    public Func<DriveItem, Task>? ShowPropertiesAsync { get; init; }

    /// <summary>Rebuilds the pane — after a delete/rename, or after pause/resume changes the badge.</summary>
    public Func<Task>? RefreshPaneAsync { get; init; }
}
