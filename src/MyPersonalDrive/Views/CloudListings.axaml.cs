using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels;
using static MyPersonalDrive.Views.RowGestures;

namespace MyPersonalDrive.Views;

/// <summary>
/// The cloud pane's listing in its three modes (list, icons, gallery), the signed-out card, the
/// empty state and the drop badge. Markup, plus what needs the visual tree: row gestures (activate,
/// multi-select, start a drag), the drop target that receives local files, and the list's keyboard
/// activation.
/// </summary>
public partial class CloudListings : UserControl
{
    private static Localizer Loc => Localizer.Instance;

    private readonly RowDragSource<DriveNodeViewModel> _rowDrag;

    public CloudListings()
    {
        InitializeComponent();
        _rowDrag = new RowDragSource<DriveNodeViewModel>(CreateTransfer, ex => ReportDragFailure(DataContext, ex));

        // Tunnel, not the plain bubble subscription a XAML PointerPressed/PointerMoved attribute
        // on the row Button itself would give: ButtonBase marks these events Handled as part of
        // its own click tracking (RoutingStrategies.Bubble), so a same-strategy handler attached
        // on the Button never saw them at all — drag-and-drop never started, full stop. Attaching
        // at the ListBox with Tunnel runs this before that happens (docs/INTERFACE_IMPROVEMENT_PLAN.md
        // Task 5).
        ListModeListing.AddHandler(InputElement.PointerPressedEvent, OnCloudRowPointerPressed, RoutingStrategies.Tunnel);
        ListModeListing.AddHandler(InputElement.PointerMovedEvent, _rowDrag.OnPointerMoved, RoutingStrategies.Tunnel);

        // The tile modes get exactly the same gestures as list mode (docs/PLAN-UX-ROUND-3.md X2).
        // They had none: an ItemsRepeater has no selection or keyboard model of its own, and the
        // three handlers above were attached to the ListBox by name, so switching view mode
        // silently dropped multi-select, drag-and-drop and any way to select a tile without
        // opening it. Nothing about them is list-specific — they resolve the row by walking up
        // from the hit element to whatever is bound to a node.
        foreach (var tiles in new Control[] { IconsModeListing, GalleryModeListing })
        {
            tiles.AddHandler(InputElement.PointerPressedEvent, OnCloudRowPointerPressed, RoutingStrategies.Tunnel);
            tiles.AddHandler(InputElement.PointerMovedEvent, _rowDrag.OnPointerMoved, RoutingStrategies.Tunnel);
        }

        // Double click opens, in all three modes and in both panes. Bubble, and only Bubble:
        // DoubleTappedEvent is registered with RoutingStrategies.Bubble alone, so a handler added
        // for Tunnel is attached to a phase the event never routes and is simply never called —
        // which is exactly what shipped, leaving the context menu's "Open" as the only way in.
        // (The pointer handlers above genuinely do tunnel: PointerPressedEvent routes both.)
        // handledEventsToo, because the row and tile roots are Buttons and the gesture reaches them
        // on the way up.
        foreach (var listing in new Control[] { ListModeListing, IconsModeListing, GalleryModeListing })
        {
            listing.AddHandler(InputElement.DoubleTappedEvent, OnCloudRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    /// <summary>Gives the list focus so arrow keys and Enter work without a click first.</summary>
    public void FocusList() => ListModeListing.Focus();

    /// <summary>Double click on a row or tile: open the folder, or preview the file (X2).</summary>
    private void OnCloudRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (NodeUnder<DriveNodeViewModel>(e.Source) is { } node)
        {
            node.ActivateCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Enter (or Space) on the focused row does what clicking it does: select a file, open a folder.
    /// The ListBox gives arrow-key movement between rows for free, but activation is the row's own
    /// Button, which is not focusable — making it focusable instead would put the row's five action
    /// buttons into the tab order ahead of the next row.
    ///
    /// Code-behind because it is a visual-tree concern: the key event, and which item the list has
    /// focused, are things the view knows and the view model deliberately does not.
    /// </summary>
    private void OnListingKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Ctrl/Cmd+A moved to OnWindowKeyDown, so that it also reaches the two tile modes
        // (docs/PLAN-UX-ROUND-3.md X5). This handler keeps what genuinely needs the list: which
        // row the ListBox has focused.

        if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space))
        {
            return;
        }

        if (sender is not ListBox { SelectedItem: DriveNodeViewModel node })
        {
            return;
        }

        e.Handled = true;
        // Fire and forget through the command, so the AsyncCommand's own error routing applies
        // rather than this handler becoming an `async void` that can take the process down.
        node.ActivateCommand.Execute(null);
    }

    private readonly DropHighlight _dropHighlight = new();

    /// <summary>
    /// The three-part drop-target affordance (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5 Phase 4):
    /// the pane's own border/background highlight, the specific folder row's highlight when the
    /// drop would land inside it rather than the pane's current path, and the "+ Subir a X" badge.
    /// Purely visual — no VM call, nothing here decides where the drop actually goes; that's
    /// resolved again, identically, in <see cref="OnCloudListingDrop"/> via the same
    /// <see cref="ResolveCloudDropTargetPath"/>.
    /// </summary>
    private void OnCloudListingDragOver(object? sender, DragEventArgs e)
    {
        var listing = sender as Control;
        var hasFormat = e.DataTransfer.Contains(LocalPathsDataFormat);
        if (!hasFormat || DataContext is not MainWindowViewModel viewModel)
        {
            e.DragEffects = DragDropEffects.None;
            ClearCloudDropHighlight(listing);
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        var hoveredNode = NodeUnder<DriveNodeViewModel>(e.Source);
        _dropHighlight.Show(listing, hoveredNode is { IsFolder: true } ? ContainerFor(e.Source, hoveredNode) : null);

        var targetPath = ResolveCloudDropTargetPath(e, viewModel);
        CloudDropOverlayText.Text = Loc.F(StringKeys.Drop.UploadTo, DisplayNameForDropTarget(targetPath, viewModel.CurrentPath));
        CloudDropOverlay.IsVisible = true;
    }

    private void OnCloudListingDragLeave(object? sender, DragEventArgs e) => ClearCloudDropHighlight(sender as Control);

    private void ClearCloudDropHighlight(Control? listing)
    {
        _dropHighlight.Clear(listing);
        CloudDropOverlay.IsVisible = false;
    }

    private async void OnCloudListingDrop(object? sender, DragEventArgs e)
    {
        ClearCloudDropHighlight(sender as Control);

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var localPaths = e.DataTransfer.TryGetValue(LocalPathsDataFormat);
        if (localPaths is not { Length: > 0 })
        {
            return;
        }

        var targetPath = ResolveCloudDropTargetPath(e, viewModel);

        // async void: an exception escaping here ends the process, and an upload can fail for a
        // dozen ordinary reasons (docs/PLAN-UX-ROUND-4.md Z1).
        try
        {
            await viewModel.HandleLocalFilesDroppedAsync(localPaths, targetPath);
        }
        catch (Exception ex)
        {
            viewModel.ReportHandlerFailure(ex);
        }
    }

    /// <summary>The folder row under the drop point, if any — otherwise the currently browsed folder.</summary>
    private static string ResolveCloudDropTargetPath(DragEventArgs e, MainWindowViewModel viewModel)
    {
        if (NodeUnder<DriveNodeViewModel>(e.Source) is { IsFolder: true } node)
        {
            return node.Path;
        }

        return viewModel.CurrentPath;
    }

    private void OnCloudRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Attached at the listing container (see the constructor), not the row itself — see the
        // matching comment on OnLocalRowPointerPressed.
        if (NodeUnder<DriveNodeViewModel>(e.Source) is not { } node || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Ctrl/Shift+Click (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2) — see the matching comment on
        // OnLocalRowPointerPressed.
        if (DataContext is MainWindowViewModel viewModel && HandleMultiSelectGesture(e, () => viewModel.ToggleSelection(node), () => viewModel.SelectRange(node)))
        {
            return;
        }

        _rowDrag.Arm(e, node);
    }

    /// <summary>
    /// Both files and folders can be dragged — `filesystem download` is recursive for folders
    /// (verified in docs/PLAN-LOCAL-SYNC.md), unlike the row's own manual `DownloadCommand`, which
    /// is folder-restricted for an unrelated, app-level reason (docs/ARCHITECTURE.md §9 item 11).
    /// </summary>
    private static DataTransfer CreateTransfer(DriveNodeViewModel node)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(CloudItemsDataFormat, new[] { node.Item }));
        return transfer;
    }
}
