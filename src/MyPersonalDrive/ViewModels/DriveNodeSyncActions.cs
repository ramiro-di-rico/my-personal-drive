using MyPersonalDrive.Models;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The context-menu callbacks a cloud row needs (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6),
/// bundled into one object rather than more constructor parameters on
/// <see cref="DriveNodeViewModel"/> — it already takes eight. Every member is optional, the same
/// way the row's existing <c>Action&lt;Exception&gt;? onError</c> is: a row built without this
/// (e.g. an older test) just gets commands that can't execute, not a null-reference crash.
/// </summary>
public sealed class DriveNodeSyncActions
{
    /// <summary>Looks up the configured sync pair (if any) whose remote side is this row's path.</summary>
    public Func<DriveItem, SyncPairViewModel?>? FindSyncPair { get; init; }

    public Func<DriveItem, Task>? SyncSelectedPathAsync { get; init; }

    public Func<DriveItem, Task>? CopyPathAsync { get; init; }

    public Func<DriveItem, Task>? UploadToFolderAsync { get; init; }

    public Func<DriveItem, Task>? DownloadHereAsync { get; init; }

    public Func<DriveItem, Task>? ShowPropertiesAsync { get; init; }

    /// <summary>
    /// Rebuilds the pane after an action that changes whether a sync-pair badge should show (pause/
    /// resume, creating a pair) — the row itself never re-queries <see cref="FindSyncPair"/> after
    /// construction, so this is how the badge catches up.
    /// </summary>
    public Func<Task>? RefreshPaneAsync { get; init; }
}
