using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Local;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The string table. Code-behind builds a dozen dialogs by hand, so it needs the same access
    /// the view models get through <c>ObservableObject.Loc</c>.
    /// </summary>
    private static Localizer Loc => Localizer.Instance;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;

        // Tunnel, not the plain bubble subscription a XAML PointerPressed/PointerMoved attribute
        // on the row Button itself would give: ButtonBase marks these events Handled as part of
        // its own click tracking (RoutingStrategies.Bubble), so a same-strategy handler attached
        // on the Button never saw them at all — drag-and-drop never started, full stop. Attaching
        // at the ListBox with Tunnel runs this before that happens (docs/INTERFACE_IMPROVEMENT_PLAN.md
        // Task 5).
        LocalListing.AddHandler(InputElement.PointerPressedEvent, OnLocalRowPointerPressed, RoutingStrategies.Tunnel);
        LocalListing.AddHandler(InputElement.PointerMovedEvent, OnLocalRowPointerMoved, RoutingStrategies.Tunnel);
        ListModeListing.AddHandler(InputElement.PointerPressedEvent, OnCloudRowPointerPressed, RoutingStrategies.Tunnel);
        ListModeListing.AddHandler(InputElement.PointerMovedEvent, OnCloudRowPointerMoved, RoutingStrategies.Tunnel);

        // The tile modes get exactly the same gestures as list mode (docs/PLAN-UX-ROUND-3.md X2).
        // They had none: an ItemsRepeater has no selection or keyboard model of its own, and the
        // three handlers above were attached to the ListBox by name, so switching view mode
        // silently dropped multi-select, drag-and-drop and any way to select a tile without
        // opening it. Nothing about them is list-specific — they resolve the row by walking up
        // from the hit element to whatever is bound to a node.
        foreach (var tiles in new Control[] { IconsModeListing, GalleryModeListing })
        {
            tiles.AddHandler(InputElement.PointerPressedEvent, OnCloudRowPointerPressed, RoutingStrategies.Tunnel);
            tiles.AddHandler(InputElement.PointerMovedEvent, OnCloudRowPointerMoved, RoutingStrategies.Tunnel);
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

        LocalListing.AddHandler(InputElement.DoubleTappedEvent, OnLocalRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        // Everything else on the keyboard (docs/PLAN-UX-ROUND-3.md X5). Bubble at the window, not
        // KeyBindings and not a per-control handler: a focused TextBox marks Ctrl+A, Delete and F2
        // as handled while it is doing its own editing, so a bubble-phase handler is guarded
        // against stealing them for free — which is exactly the concern that kept Ctrl+A scoped to
        // one ListBox before, and the reason the tile modes never got it (an ItemsRepeater takes no
        // focus, so it has no KeyDown of its own to hang anything on).
        KeyDown += OnWindowKeyDown;

        // One write per gesture, not one per tick — same reasoning as the console handle below
        // (docs/PLAN-UX-ROUND-4.md Y6). PointerCaptureLost is the slider's end-of-drag; LostFocus
        // covers the arrow keys, which change the value without a pointer ever being involved.
        ViewerZoomSlider.PointerCaptureLost += (_, _) => CommitZoom();
        ViewerZoomSlider.LostFocus += (_, _) => CommitZoom();

        // The console's own resize handle (docs/PLAN-UX-ROUND-3.md X7).
        ConsoleResizeHandle.PointerPressed += OnConsoleResizeStarted;
        ConsoleResizeHandle.PointerMoved += OnConsoleResizeMoved;
        ConsoleResizeHandle.PointerReleased += OnConsoleResizeFinished;
    }

    private void CommitZoom()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CommitViewerZoom();
        }
    }

    private Point? _consoleResizeLastPoint;

    /// <summary>
    /// Dragging the handle changes the console body's height. Pointer capture rather than a
    /// window-level handler: without it the drag stops the moment the pointer leaves the 6px strip,
    /// which is immediately.
    /// </summary>
    private void OnConsoleResizeStarted(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _consoleResizeLastPoint = e.GetPosition(this);
        e.Pointer.Capture(ConsoleResizeHandle);
        e.Handled = true;
    }

    private void OnConsoleResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_consoleResizeLastPoint is not { } last || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var current = e.GetPosition(this);
        // One step per move rather than an absolute offset from the press: the view model clamps,
        // so an absolute delta would keep accumulating past the limit and the handle would then
        // need that distance dragged back before the console moved again.
        viewModel.Console.ResizeCommandConsole(current.Y - last.Y);
        _consoleResizeLastPoint = current;
        e.Handled = true;
    }

    private void OnConsoleResizeFinished(object? sender, PointerReleasedEventArgs e)
    {
        // The height is persisted here rather than on every move: one write per drag, not one per
        // pointer event (docs/PLAN-UX-ROUND-4.md Y6).
        if (_consoleResizeLastPoint is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Console.CommitCommandConsoleHeight();
        }

        _consoleResizeLastPoint = null;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// The node a pointer event landed on, whichever container materialized it — a ListBoxItem in
    /// list mode, a tile Border in the other two. DataContext is inherited, so walking up from the
    /// hit element (usually a TextBlock or a Path) finds the node without knowing the container
    /// type at all.
    /// </summary>
    private static T? NodeUnder<T>(object? source) where T : class
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
    private static Control? ContainerFor(object? source, object node)
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

    /// <summary>Double click on a row or tile: open the folder, or preview the file (X2).</summary>
    private void OnCloudRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (NodeUnder<DriveNodeViewModel>(e.Source) is { } node)
        {
            node.ActivateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLocalRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (NodeUnder<LocalNodeViewModel>(e.Source) is { } node)
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

    /// <summary>
    /// Everything the keyboard can reach that is not a row activation (docs/PLAN-UX-ROUND-3.md X5).
    /// Before this the entire inventory was Ctrl+, / Ctrl+~ / Ctrl+A / Enter — no F5, no F2, no
    /// Delete, no way back up a folder, no way to the search box, and Escape did not close the
    /// viewer.
    ///
    /// Bubble phase, so a control that is genuinely using the key has already marked it handled: a
    /// TextBox consumes Ctrl+A, Delete and F2 while it is being edited, which is the guard that
    /// keeps these from firing mid-typing. Anything that must work even inside a text box (F5,
    /// Escape) is keyed off gestures a TextBox does not claim.
    /// </summary>
    private void OnWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        switch (e.Key)
        {
            // Closes the viewer panel and nothing else: unguarded, Escape would also be the way out
            // of a dialog, and those are separate windows with their own IsCancel buttons.
            case Avalonia.Input.Key.Escape when viewModel.IsViewerVisible:
                Run(viewModel.CloseViewerCommand);
                break;

            case Avalonia.Input.Key.F5:
                Run(ActivePaneIsLocal() ? viewModel.LocalExplorer.RefreshCommand : viewModel.RefreshCommand);
                break;

            case Avalonia.Input.Key.N when control && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Run(viewModel.CreateFolderCommand);
                break;

            case Avalonia.Input.Key.F when control:
                (ActivePaneIsLocal() ? LocalSearchBox : CloudSearchBox).Focus();
                e.Handled = true;
                break;

            case Avalonia.Input.Key.A when control:
                Run(ActivePaneIsLocal() ? viewModel.LocalExplorer.SelectAllCommand : viewModel.SelectAllRowsCommand);
                break;

            // Back a folder. Backspace is the file-manager idiom; Alt+Left is the browser one, and
            // both are free here because neither pane hosts an editable surface that wants them.
            case Avalonia.Input.Key.Back:
            case Avalonia.Input.Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                Run(ActivePaneIsLocal() ? viewModel.LocalExplorer.BackCommand : viewModel.BackCommand);
                break;

            // F2 renames exactly one row. With several marked there is no single name to edit, and
            // silently renaming the first would be worse than doing nothing.
            case Avalonia.Input.Key.F2 when ActivePaneIsLocal():
                Run(SingleSelected(viewModel.LocalExplorer.Items, node => node.IsSelected)?.RenameCommand);
                break;

            case Avalonia.Input.Key.F2:
                Run(SingleSelected(viewModel.RootItems, node => node.IsSelected)?.RenameCommand);
                break;

            // Delete acts on the whole selection, and goes through the same command the buttons
            // use — so it inherits their confirmation prompt rather than deleting on a keypress.
            case Avalonia.Input.Key.Delete when ActivePaneIsLocal():
                Run(viewModel.LocalExplorer.DeleteSelectedCommand);
                break;

            case Avalonia.Input.Key.Delete:
                Run(viewModel.TrashSelectedCommand);
                break;
        }

        void Run(AsyncCommand? command)
        {
            if (command?.CanExecute(null) != true)
            {
                return;
            }

            e.Handled = true;
            // Through the command, so AsyncCommand's error routing applies and this handler never
            // becomes an `async void` that can take the process down.
            command.Execute(null);
        }
    }

    /// <summary>The one row marked in a pane, or null when zero or several are.</summary>
    private static T? SingleSelected<T>(IEnumerable<T> rows, Func<T, bool> isSelected) where T : class
    {
        T? found = null;
        foreach (var row in rows.Where(isSelected))
        {
            if (found is not null)
            {
                return null;
            }

            found = row;
        }

        return found;
    }

    /// <summary>
    /// Which pane a keystroke belongs to. Focus decides it, and the cloud pane is the default —
    /// the local pane can be hidden entirely, so "wherever focus last was" has to fall back to the
    /// pane that is always there.
    /// </summary>
    private bool ActivePaneIsLocal()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        for (var visual = focused; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, LocalPane))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Avalonia's <see cref="GridSplitter"/> drags star-sized columns for free but has no built-in
    /// reset gesture; double-click puts the cloud/local split back to 50/50
    /// (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 3). Code-behind because it manipulates the visual
    /// tree's own `Grid.ColumnDefinitions`, not view-model state.
    /// </summary>
    private void ExplorerSplitter_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not Control { Parent: Grid grid })
        {
            return;
        }

        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLocalExplorerPanelVisible) && sender is MainWindowViewModel viewModel)
        {
            ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);
        }
    }

    private readonly ExplorerSplitState _explorerSplit = new();

    /// <summary>
    /// Collapses/restores the local pane's star-sized column directly, since toggling `IsVisible`
    /// on its content (done via binding in XAML) has no effect on a `*` column's own width — unlike
    /// an `Auto` column, which already shrinks to 0 the moment its content stops participating in
    /// layout. Which widths to save and restore is <see cref="ExplorerSplitState"/>'s decision, so
    /// it can be tested without a rendered window.
    /// </summary>
    private void ApplyLocalExplorerPanelColumnWidth(bool visible)
    {
        var remote = ExplorerColumnsGrid.ColumnDefinitions[0];
        var local = ExplorerColumnsGrid.ColumnDefinitions[2];

        var (newRemote, newLocal) = visible
            ? _explorerSplit.Restore()
            : _explorerSplit.Collapse(remote.Width, local.Width);

        remote.Width = newRemote;
        local.Width = newLocal;
    }

    // In-process only — never crosses the app boundary, so an arbitrary string identifier (not a
    // registered clipboard/OS format) is fine as the payload's identity.
    private static readonly DataFormat<string[]> LocalPathsDataFormat = DataFormat.CreateInProcessFormat<string[]>("application/x-mypersonaldrive-local-paths");

    // How far the pointer has to move, while pressed, before a local-pane row press is treated as
    // a drag rather than the click that navigates into a folder (docs/INTERFACE_IMPROVEMENT_PLAN.md
    // Task 5 Phase 2). Below this, PointerReleased still reaches the row's own Button normally.
    private const double DragStartThresholdPixels = 4;

    private Point? _localDragStartPoint;
    private LocalNodeViewModel? _localDragCandidate;
    private PointerPressedEventArgs? _localDragPressedArgs;

    private void OnLocalRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Attached at the ListBox (see the constructor), not the row itself, so the row has to be
        // resolved by walking up from whatever was actually hit — usually the icon/text inside it.
        if (NodeUnder<LocalNodeViewModel>(e.Source) is not { } node || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Ctrl/Shift+Click (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2) is a selection gesture, not an
        // activation or a drag start: handling it here, before the Button's own Click fires (this
        // handler runs on Tunnel — see the constructor's comment), stops it from also opening a
        // folder or resetting the multi-selection back down to one row.
        if (DataContext is MainWindowViewModel { LocalExplorer: { } explorer } && HandleMultiSelectGesture(e, () => explorer.ToggleSelection(node), () => explorer.SelectRange(node)))
        {
            return;
        }

        _localDragStartPoint = e.GetPosition(null);
        _localDragCandidate = node;
        _localDragPressedArgs = e;
    }

    /// <summary>
    /// Shared Ctrl/Shift-click routing for both panes' row-pressed handlers. Returns true (and
    /// marks the event handled) when a modifier gesture was recognized and acted on, so the caller
    /// skips its own plain-click handling (drag-start tracking, which would otherwise arm on a
    /// selection gesture too).
    /// </summary>
    private static bool HandleMultiSelectGesture(PointerPressedEventArgs e, Action toggleSelection, Action selectRange)
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

    /// <summary>
    /// Both files and folders can be dragged — <c>filesystem upload</c> already accepts folder
    /// paths (its <c>-d</c>/folder-conflict-strategy flag exists for exactly this), unlike the
    /// row's own manual <c>DownloadCommand</c>, which is folder-restricted for an unrelated reason
    /// (no UI need for it before now, not a CLI limitation — see docs/ARCHITECTURE.md §9 item 11).
    /// </summary>
    private async void OnLocalRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_localDragStartPoint is not { } start || _localDragCandidate is not { } node || _localDragPressedArgs is not { } pressedArgs)
        {
            return;
        }

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _localDragStartPoint = null;
            _localDragCandidate = null;
            _localDragPressedArgs = null;
            return;
        }

        var current = e.GetPosition(null);
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < DragStartThresholdPixels)
        {
            return;
        }

        _localDragStartPoint = null;
        _localDragCandidate = null;
        _localDragPressedArgs = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(LocalPathsDataFormat, new[] { node.Item.Path }));
        try
        {
            await DragDrop.DoDragDropAsync(pressedArgs, transfer, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            // This is async void — an exception escaping here would otherwise crash the process.
            if (DataContext is MainWindowViewModel vm)
            {
                vm.StatusMessage = Loc.F(StringKeys.Drop.Error, ex.Message);
            }
        }
    }

    private Control? _cloudHighlightedDropRow;

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
        listing?.Classes.Add("dropTarget");

        var hoveredNode = NodeUnder<DriveNodeViewModel>(e.Source);
        var targetsAFolderRow = hoveredNode is { IsFolder: true };
        var hoveredRow = targetsAFolderRow ? ContainerFor(e.Source, hoveredNode!) : null;
        if (!ReferenceEquals(hoveredRow, _cloudHighlightedDropRow) || !targetsAFolderRow)
        {
            _cloudHighlightedDropRow?.Classes.Remove("dropTarget");
            _cloudHighlightedDropRow = targetsAFolderRow ? hoveredRow : null;
            _cloudHighlightedDropRow?.Classes.Add("dropTarget");
        }

        var targetPath = ResolveCloudDropTargetPath(e, viewModel);
        CloudDropOverlayText.Text = Loc.F(StringKeys.Drop.UploadTo, DisplayNameForDropTarget(targetPath, viewModel.CurrentPath));
        CloudDropOverlay.IsVisible = true;
    }

    private void OnCloudListingDragLeave(object? sender, DragEventArgs e) => ClearCloudDropHighlight(sender as Control);

    private void ClearCloudDropHighlight(Control? listing)
    {
        listing?.Classes.Remove("dropTarget");
        _cloudHighlightedDropRow?.Classes.Remove("dropTarget");
        _cloudHighlightedDropRow = null;
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

    /// <summary>"the current folder" for the folder already open, otherwise that folder's own name.</summary>
    private static string DisplayNameForDropTarget(string targetPath, string currentPath)
    {
        if (string.Equals(targetPath, currentPath, StringComparison.Ordinal))
        {
            return Loc.T(StringKeys.Drop.CurrentFolder);
        }

        var trimmed = targetPath.TrimEnd('/');
        var lastSeparator = trimmed.LastIndexOf('/');
        return lastSeparator >= 0 && lastSeparator < trimmed.Length - 1 ? trimmed[(lastSeparator + 1)..] : trimmed;
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

    // Same in-process-only reasoning as LocalPathsDataFormat, for the opposite direction
    // (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5 Phase 3).
    private static readonly DataFormat<DriveItem[]> CloudItemsDataFormat = DataFormat.CreateInProcessFormat<DriveItem[]>("application/x-mypersonaldrive-cloud-items");

    private Point? _cloudDragStartPoint;
    private DriveNodeViewModel? _cloudDragCandidate;
    private PointerPressedEventArgs? _cloudDragPressedArgs;

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

        _cloudDragStartPoint = e.GetPosition(null);
        _cloudDragCandidate = node;
        _cloudDragPressedArgs = e;
    }

    /// <summary>
    /// Both files and folders can be dragged — `filesystem download` is recursive for folders
    /// (verified in docs/PLAN-LOCAL-SYNC.md), unlike the row's own manual `DownloadCommand`, which
    /// is folder-restricted for an unrelated, app-level reason (docs/ARCHITECTURE.md §9 item 11).
    /// </summary>
    private async void OnCloudRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_cloudDragStartPoint is not { } start || _cloudDragCandidate is not { } node || _cloudDragPressedArgs is not { } pressedArgs)
        {
            return;
        }

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _cloudDragStartPoint = null;
            _cloudDragCandidate = null;
            _cloudDragPressedArgs = null;
            return;
        }

        var current = e.GetPosition(null);
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < DragStartThresholdPixels)
        {
            return;
        }

        _cloudDragStartPoint = null;
        _cloudDragCandidate = null;
        _cloudDragPressedArgs = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(CloudItemsDataFormat, new[] { node.Item }));
        try
        {
            await DragDrop.DoDragDropAsync(pressedArgs, transfer, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            // This is async void — an exception escaping here would otherwise crash the process.
            if (DataContext is MainWindowViewModel vm)
            {
                vm.StatusMessage = Loc.F(StringKeys.Drop.Error, ex.Message);
            }
        }
    }

    private Control? _localHighlightedDropRow;

    /// <summary>Mirrors <see cref="OnCloudListingDragOver"/> for the opposite direction — see its doc comment.</summary>
    private void OnLocalListingDragOver(object? sender, DragEventArgs e)
    {
        var listBox = sender as ListBox;
        var hasFormat = e.DataTransfer.Contains(CloudItemsDataFormat);
        if (!hasFormat || DataContext is not MainWindowViewModel viewModel)
        {
            e.DragEffects = DragDropEffects.None;
            ClearLocalDropHighlight(listBox);
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        listBox?.Classes.Add("dropTarget");

        var hoveredRow = e.Source is Visual visual ? visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) : null;
        var targetsAFolderRow = hoveredRow?.DataContext is LocalNodeViewModel { IsFolder: true };
        if (!ReferenceEquals(hoveredRow, _localHighlightedDropRow) || !targetsAFolderRow)
        {
            _localHighlightedDropRow?.Classes.Remove("dropTarget");
            _localHighlightedDropRow = targetsAFolderRow ? hoveredRow : null;
            _localHighlightedDropRow?.Classes.Add("dropTarget");
        }

        var targetPath = ResolveLocalDropTargetPath(e, viewModel);
        LocalDropOverlayText.Text = Loc.F(StringKeys.Drop.DownloadTo, DisplayNameForDropTarget(targetPath, viewModel.LocalExplorer.CurrentPath));
        LocalDropOverlay.IsVisible = true;
    }

    private void OnLocalListingDragLeave(object? sender, DragEventArgs e) => ClearLocalDropHighlight(sender as ListBox);

    private void ClearLocalDropHighlight(ListBox? listBox)
    {
        listBox?.Classes.Remove("dropTarget");
        _localHighlightedDropRow?.Classes.Remove("dropTarget");
        _localHighlightedDropRow = null;
        LocalDropOverlay.IsVisible = false;
    }

    private async void OnLocalListingDrop(object? sender, DragEventArgs e)
    {
        ClearLocalDropHighlight(sender as ListBox);

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var items = e.DataTransfer.TryGetValue(CloudItemsDataFormat);
        if (items is not { Length: > 0 })
        {
            return;
        }

        var targetPath = ResolveLocalDropTargetPath(e, viewModel);

        // See OnCloudListingDrop: async void, so nothing else would catch this.
        try
        {
            await viewModel.HandleCloudItemsDroppedAsync(items, targetPath);
        }
        catch (Exception ex)
        {
            viewModel.ReportHandlerFailure(ex);
        }
    }

    /// <summary>The local folder row under the drop point, if any — otherwise the currently browsed local folder.</summary>
    private static string ResolveLocalDropTargetPath(DragEventArgs e, MainWindowViewModel viewModel)
    {
        if (e.Source is Visual visual)
        {
            var listBoxItem = visual.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (listBoxItem?.DataContext is LocalNodeViewModel { IsFolder: true } node)
            {
                return node.Item.Path;
            }
        }

        return viewModel.LocalExplorer.CurrentPath;
    }

    private async void BrowseCliPath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // async void: the picker goes through the desktop portal, which can fail, and the assignment
        // below writes settings.json (docs/PLAN-UX-ROUND-4.md Z1).
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T(StringKeys.Picker.CliPathTitle),
                AllowMultiple = false
            });

            if (files.Count == 0)
            {
                return;
            }

            viewModel.CliPath = files[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            viewModel.ReportHandlerFailure(ex);
        }
    }

    private async void BrowseDefaultSyncFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // See BrowseCliPath.
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Loc.T(StringKeys.Picker.DefaultSyncFolderTitle),
                AllowMultiple = false
            });

            if (folders.Count == 0)
            {
                return;
            }

            viewModel.DefaultSyncFolder = folders[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            viewModel.ReportHandlerFailure(ex);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.RequestUploadFilesAsync = PickUploadFilesAsync;
        viewModel.RequestConflictStrategyAsync = files => Dialogs.UploadConflictDialog.ShowAsync(this, files);
        viewModel.RequestRenameAsync = PromptForRenameAsync;
        viewModel.RequestCopyNameAsync = PromptForCopyNameAsync;
        viewModel.RequestCreateFolderAsync = PromptForNewFolderNameAsync;
        viewModel.RequestDownloadFolderAsync = PickDownloadFolderAsync;
        viewModel.Console.RequestSaveActivityAsync = PickSaveActivityAsync;
        viewModel.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(this, question);
        viewModel.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.RequestShowPropertiesAsync = (title, fields) => Dialogs.PropertiesDialog.ShowAsync(this, title, fields);

        viewModel.LocalExplorer.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(this, question);
        viewModel.LocalExplorer.RequestRenameAsync = PromptForRenameAsync;
        viewModel.LocalExplorer.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.LocalExplorer.RequestShowPropertiesAsync = (title, fields) => Dialogs.PropertiesDialog.ShowAsync(this, title, fields);


        // ExplorerColumnsGrid.ColumnDefinitions[2] is star-sized so the splitter can resize it —
        // which also means it doesn't shrink to 0 on its own just because IsVisible on its content
        // goes false, the way an Auto column (the Status sidebar's) does. Kept in sync here instead.
        viewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        viewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);

        viewModel.SyncPanel.RequestNewPairAsync = prefill => Dialogs.SyncPairDialog.ShowAddAsync(this, viewModel.SyncPanel, viewModel.RootPath, prefill);
        viewModel.SyncPanel.RequestPreviewConfirmationAsync = (plan, warnings) => Dialogs.SyncPreviewDialog.ShowAsync(this, plan, warnings);
        viewModel.SyncPanel.RequestConflictResolutionsAsync = conflicts => Dialogs.SyncConflictsDialog.ShowAsync(this, conflicts);
        viewModel.SyncPanel.RequestFailureReviewAsync = failures => Dialogs.SyncFailuresDialog.ShowAsync(this, failures);
        viewModel.SyncPanel.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(this, question);
        viewModel.SyncPanel.RequestEditPairAsync = pair => Dialogs.SyncPairDialog.ShowEditAsync(this, pair);
        viewModel.SyncPanel.RequestAlertAsync = message => Dialogs.AlertDialog.ShowAsync(this, message);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.InitializeAsync();
            }
            catch
            {
                // The view-model already surfaced the error in the status panel.
            }

            // Something has to be focused for arrow keys and Enter to do anything, and nothing was:
            // the app opened with focus nowhere, so the keyboard did not work until the user had
            // clicked a row first (docs/PLAN-UX-ROUND-3.md X5). Only in list mode — an
            // ItemsRepeater takes no focus, and the window-level shortcuts default to the cloud
            // pane anyway, so the tile modes lose nothing by starting unfocused.
            if (viewModel.IsListView)
            {
                ListModeListing.Focus();
            }
        }
    }

    private async Task<IReadOnlyList<string>> PickUploadFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T(StringKeys.Picker.UploadTitle),
            AllowMultiple = true
        });

        return files.Select(file => file.Path.LocalPath).ToList();
    }

    private async Task<string?> PickDownloadFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T(StringKeys.Picker.DownloadFolderTitle),
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    private Task<string?> PromptForRenameAsync(string currentName)
        => Dialogs.NamePromptDialog.ShowAsync(
            this,
            Loc.T(StringKeys.Dialog.RenameTitle),
            Loc.F(StringKeys.Dialog.RenamePrompt, currentName),
            Loc.T(StringKeys.Menu.Rename),
            initialText: currentName,
            placeholder: null,
            mustDifferFrom: currentName);

    private Task<string?> PromptForNewFolderNameAsync()
        => Dialogs.NamePromptDialog.ShowAsync(
            this,
            Loc.T(StringKeys.Dialog.NewFolderTitle),
            Loc.T(StringKeys.Dialog.NewFolderPrompt),
            Loc.T(StringKeys.Common.Create),
            initialText: null,
            placeholder: Loc.T(StringKeys.Dialog.NewFolderPlaceholder));

    private Task<string?> PromptForCopyNameAsync(string currentName)
        => Dialogs.NamePromptDialog.ShowAsync(
            this,
            Loc.T(StringKeys.Menu.Copy),
            Loc.F(StringKeys.Dialog.CopyPrompt, currentName),
            Loc.T(StringKeys.Common.Copy),
            initialText: null,
            placeholder: Loc.T(StringKeys.Dialog.CopyPlaceholder),
            mustDifferFrom: currentName);



    private async Task<string?> PickSaveActivityAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(StringKeys.Picker.SaveLogTitle),
            SuggestedFileName = "cli-activity.log",
            DefaultExtension = "log"
        });

        return file?.Path.LocalPath;
    }







    /// <summary>Puts text on the system clipboard — "Copiar ruta" (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6). Silently a no-op if the platform offers no clipboard (e.g. a headless test host).</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }





}
