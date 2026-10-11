using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views;

/// <summary>
/// What the cloud and local panes share for row gestures: the in-process drag formats, the
/// hit-testing that finds the node under the pointer, the Ctrl/Shift selection gesture and the
/// drop badge's wording. Each pane owns its own drag-and-drop handlers; the two directions only
/// have to agree on this.
/// </summary>
internal static class RowGestures
{
    private static Localizer Loc => Localizer.Instance;

    // In-process only — never crosses the app boundary, so an arbitrary string identifier (not a
    // registered clipboard/OS format) is fine as the payload's identity.
    internal static readonly DataFormat<string[]> LocalPathsDataFormat = DataFormat.CreateInProcessFormat<string[]>("application/x-mypersonaldrive-local-paths");

    // How far the pointer has to move, while pressed, before a local-pane row press is treated as
    // a drag rather than the click that navigates into a folder (docs/INTERFACE_IMPROVEMENT_PLAN.md
    // Task 5 Phase 2). Below this, PointerReleased still reaches the row's own Button normally.
    internal const double DragStartThresholdPixels = 4;

    // Same in-process-only reasoning as LocalPathsDataFormat, for the opposite direction
    // (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5 Phase 3).
    internal static readonly DataFormat<DriveItem[]> CloudItemsDataFormat = DataFormat.CreateInProcessFormat<DriveItem[]>("application/x-mypersonaldrive-cloud-items");

    /// <summary>
    /// The node a pointer event landed on, whichever container materialized it — a ListBoxItem in
    /// list mode, a tile Border in the other two. DataContext is inherited, so walking up from the
    /// hit element (usually a TextBlock or a Path) finds the node without knowing the container
    /// type at all.
    /// </summary>
    internal static T? NodeUnder<T>(object? source) where T : class
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is StyledElement { DataContext: T node })
            {
                return node;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }

    /// <summary>
    /// The row or tile itself: the outermost element still bound to <paramref name="node"/>. The
    /// innermost one is whatever was hit, which is no use for a highlight class — that has to go on
    /// the container the .dropTarget styles select.
    /// </summary>
    internal static Control? ContainerFor(object? source, object node)
    {
        Control? outermost = null;
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is Control control && ReferenceEquals(control.DataContext, node))
            {
                outermost = control;
            }
            else if (outermost is not null)
            {
                break;
            }

            visual = visual.GetVisualParent();
        }

        return outermost;
    }

    /// <summary>
    /// Shared Ctrl/Shift-click routing for both panes' row-pressed handlers. Returns true (and
    /// marks the event handled) when a modifier gesture was recognized and acted on, so the caller
    /// skips its own plain-click handling (drag-start tracking, which would otherwise arm on a
    /// selection gesture too).
    /// </summary>
    internal static bool HandleMultiSelectGesture(PointerPressedEventArgs e, Action toggleSelection, Action selectRange)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            selectRange();
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            toggleSelection();
        }
        else
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    /// <summary>"the current folder" for the folder already open, otherwise that folder's own name.</summary>
    internal static string DisplayNameForDropTarget(string targetPath, string currentPath)
    {
        if (string.Equals(targetPath, currentPath, StringComparison.Ordinal))
        {
            return Loc.T(StringKeys.Drop.CurrentFolder);
        }

        var trimmed = targetPath.TrimEnd('/');
        var lastSeparator = trimmed.LastIndexOf('/');
        return lastSeparator >= 0 && lastSeparator < trimmed.Length - 1 ? trimmed[(lastSeparator + 1)..] : trimmed;
    }

    /// <summary>A drag that could not start or finish, surfaced in the status panel.</summary>
    internal static void ReportDragFailure(object? dataContext, Exception exception)
    {
        if (dataContext is MyPersonalDrive.ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.StatusMessage = Loc.F(StringKeys.Drop.Error, exception.Message);
        }
    }
}
