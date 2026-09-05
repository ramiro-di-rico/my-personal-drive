using Avalonia.Controls;

namespace MyPersonalDrive.Views;

/// <summary>
/// Remembers the explorer's column split across a hide/show of the local pane
/// (docs/PLAN-UX-ROUND-2.md §15).
///
/// Extracted from <see cref="MainWindow"/>'s code-behind so the rule can be tested: the bug it
/// fixes was entirely in *which* widths get saved and restored, and nothing about it needs a
/// rendered window. <see cref="GridLength"/> is a plain value type, so this stays honest about the
/// thing it is actually deciding.
/// </summary>
internal sealed class ExplorerSplitState
{
    private static readonly GridLength EvenShare = new(1, GridUnitType.Star);

    private GridLength? _remote;
    private GridLength? _local;

    /// <summary>A column the layout has already collapsed — zero, and absolute rather than a share.</summary>
    public static bool IsCollapsed(GridLength width) => width.IsAbsolute && width.Value <= 0;

    /// <summary>
    /// The widths to apply when hiding the local pane, recording the current split first.
    ///
    /// The remote column is reset to a full share rather than left alone: dragging the splitter
    /// rewrites both adjacent columns, so leaving the remote one at a dragged absolute width would
    /// strand empty space where the local pane used to be.
    /// </summary>
    public (GridLength Remote, GridLength Local) Collapse(GridLength remote, GridLength local)
    {
        // Guarded so a second collapse — or one applied from persisted settings at startup, before
        // the user has touched anything — cannot record the collapsed state as the width to
        // return to.
        if (!IsCollapsed(local))
        {
            _remote = remote;
            _local = local;
        }

        return (EvenShare, new GridLength(0));
    }

    /// <summary>
    /// The widths to apply when showing the local pane again: the split the user last had, or an
    /// even one if they never moved it.
    ///
    /// Restoring the remote column too is the whole point. Putting only the local column back to a
    /// full share left the remote one at whatever absolute width the last drag had given it, so the
    /// local pane returned as a sliver the user had to drag the splitter to see.
    /// </summary>
    public (GridLength Remote, GridLength Local) Restore()
        => (_remote ?? EvenShare, _local ?? EvenShare);
}
