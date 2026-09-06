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

        // Double click opens, in all three modes and in both panes. Tunnel for the same reason the
        // pointer handlers tunnel: the row and tile roots are Buttons, which handle the gesture on
        // the way back up.
        foreach (var listing in new Control[] { ListModeListing, IconsModeListing, GalleryModeListing })
        {
            listing.AddHandler(InputElement.DoubleTappedEvent, OnCloudRowDoubleTapped, RoutingStrategies.Tunnel);
        }

        LocalListing.AddHandler(InputElement.DoubleTappedEvent, OnLocalRowDoubleTapped, RoutingStrategies.Tunnel);
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
        // Ctrl/Cmd+A (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2) — scoped to this ListBox's own
        // KeyDown rather than a window-level KeyBinding, so it never steals the same gesture from a
        // focused TextBox (the search box, the CLI log filter) selecting its own text instead.
        if (e.Key == Avalonia.Input.Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectAllRowsCommand.Execute(null);
            e.Handled = true;
            return;
        }

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

    /// <summary>The local pane's counterpart to <see cref="OnListingKeyDown"/>'s Ctrl/Cmd+A handling — the local pane has no Enter/Space activation to also cover, since its rows aren't focusable buttons the way the cloud pane's are.</summary>
    private void OnLocalListingKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainWindowViewModel { LocalExplorer: { } explorer })
        {
            explorer.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
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
        await viewModel.HandleLocalFilesDroppedAsync(localPaths, targetPath);
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
        await viewModel.HandleCloudItemsDroppedAsync(items, targetPath);
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

    private async void BrowseDefaultSyncFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.RequestUploadFilesAsync = PickUploadFilesAsync;
        viewModel.RequestConflictStrategyAsync = PickConflictStrategyAsync;
        viewModel.RequestRenameAsync = PromptForRenameAsync;
        viewModel.RequestCopyNameAsync = PromptForCopyNameAsync;
        viewModel.RequestCreateFolderAsync = PromptForNewFolderNameAsync;
        viewModel.RequestDownloadFolderAsync = PickDownloadFolderAsync;
        viewModel.RequestSaveActivityAsync = PickSaveActivityAsync;
        viewModel.RequestConfirmationAsync = AskAsync;
        viewModel.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.RequestShowPropertiesAsync = ShowPropertiesAsync;

        viewModel.LocalExplorer.RequestConfirmationAsync = AskAsync;
        viewModel.LocalExplorer.RequestRenameAsync = PromptForRenameAsync;
        viewModel.LocalExplorer.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.LocalExplorer.RequestShowPropertiesAsync = ShowPropertiesAsync;


        // ExplorerColumnsGrid.ColumnDefinitions[2] is star-sized so the splitter can resize it —
        // which also means it doesn't shrink to 0 on its own just because IsVisible on its content
        // goes false, the way an Auto column (the Status sidebar's) does. Kept in sync here instead.
        viewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        viewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);

        viewModel.SyncPanel.RequestNewPairAsync = prefill => PromptForNewPairAsync(viewModel.SyncPanel, viewModel.RootPath, prefill);
        viewModel.SyncPanel.RequestPreviewConfirmationAsync = ShowPreviewAsync;
        viewModel.SyncPanel.RequestConflictResolutionsAsync = ShowConflictsAsync;
        viewModel.SyncPanel.RequestFailureReviewAsync = ShowFailuresAsync;
        viewModel.SyncPanel.RequestConfirmationAsync = AskAsync;
        viewModel.SyncPanel.RequestEditPairAsync = PromptForEditPairAsync;
        viewModel.SyncPanel.RequestAlertAsync = ShowAlertAsync;
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

    private async Task<string?> PromptForRenameAsync(string currentName)
    {
        var textBox = new TextBox
        {
            Text = currentName,
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.RenameTitle),
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = Loc.F(StringKeys.Dialog.RenamePrompt, currentName), FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = Loc.T(StringKeys.Menu.Rename), IsDefault = true, Width = 80 },
                            new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        string? result = null;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var renameButton = (Button)buttonsPanel.Children[0];
        var cancelButton = (Button)buttonsPanel.Children[1];

        renameButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> PromptForNewFolderNameAsync()
    {
        var textBox = new TextBox
        {
            PlaceholderText = Loc.T(StringKeys.Dialog.NewFolderPlaceholder),
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.NewFolderTitle),
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = Loc.T(StringKeys.Dialog.NewFolderPrompt), FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = Loc.T(StringKeys.Common.Create), IsDefault = true, Width = 80 },
                            new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        string? result = null;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var createButton = (Button)buttonsPanel.Children[0];
        var cancelButton = (Button)buttonsPanel.Children[1];

        createButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> PromptForCopyNameAsync(string currentName)
    {
        var textBox = new TextBox
        {
            PlaceholderText = Loc.T(StringKeys.Dialog.CopyPlaceholder),
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Menu.Copy),
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = Loc.F(StringKeys.Dialog.CopyPrompt, currentName), FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = Loc.T(StringKeys.Common.Copy), IsDefault = true, Width = 80 },
                            new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        string? result = null;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var copyButton = (Button)buttonsPanel.Children[0];
        var cancelButton = (Button)buttonsPanel.Children[1];

        copyButton.Click += (_, _) =>
        {
            result = textBox.Text ?? string.Empty;
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<UploadConflictStrategy> PickConflictStrategyAsync(IReadOnlyList<string> conflictingFiles)
    {
        var filesList = string.Join("\n", conflictingFiles.Take(10).Select(f => "- " + f));
        if (conflictingFiles.Count > 10)
        {
            filesList += Loc.Plural(StringKeys.Common.More, conflictingFiles.Count - 10);
        }

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.UploadConflictTitle),
            Width = 450,
            Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = Loc.T(StringKeys.Dialog.UploadConflictIntro), FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = filesList, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = Loc.T(StringKeys.Dialog.UploadConflictQuestion), FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Vertical,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Children =
                        {
                            new Button { Content = Loc.T(StringKeys.Dialog.UploadConflictKeepBoth), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.KeepBoth },
                            new Button { Content = Loc.T(StringKeys.Dialog.UploadConflictReplace), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Replace },
                            new Button { Content = Loc.T(StringKeys.Dialog.UploadConflictSkip), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Skip },
                            new Button { Content = Loc.T(StringKeys.Common.Cancel), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.None }
                        }
                    }
                }
            }
        };

        var result = UploadConflictStrategy.None;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[3];
        foreach (var child in buttonsPanel.Children)
        {
            if (child is Button button)
            {
                button.Click += (_, _) =>
                {
                    result = (UploadConflictStrategy)button.Tag!;
                    dialog.Close();
                };
            }
        }

        await dialog.ShowDialog(this);
        return result;
    }

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

    /// <summary>
    /// "Add sync pair", with the remote folder browser as a second face of the same dialog
    /// (swapping its Content) instead of a window stacked on top of it — one modal, not two.
    /// </summary>
    private async Task<NewSyncPairRequest?> PromptForNewPairAsync(SyncPanelViewModel syncPanel, string remoteRootPath, SyncPairPrefill? prefill = null)
    {
        var remoteBox = new TextBox { PlaceholderText = "/my-files/Documents", Width = 280, Text = prefill?.RemotePath };
        var remoteBrowseButton = new Button { Content = Loc.T(StringKeys.Common.Browse), IsVisible = syncPanel.GetRemoteFolderChildren is not null };
        var localBox = new TextBox { Width = 280, IsReadOnly = true, PlaceholderText = Loc.T(StringKeys.Dialog.PairLocalFolderPlaceholder), Text = prefill?.LocalPath };
        var browseButton = new Button { Content = Loc.T(StringKeys.Common.Browse) };

        // RemoteToLocal stays first, and therefore the default: it's the only direction that
        // cannot destroy anything in the cloud (docs/PLAN-LOCAL-SYNC.md §15).
        var directionBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                Loc.T(StringKeys.Dialog.PairDirectionDownload),
                Loc.T(StringKeys.Dialog.PairDirectionUpload),
                Loc.T(StringKeys.Dialog.PairDirectionTwoWay),
            },
            SelectedIndex = 0,
        };

        var policyBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                Loc.T(StringKeys.Dialog.PairPolicyAsk),
                Loc.T(StringKeys.Dialog.PairPolicyKeepBoth),
                Loc.T(StringKeys.Dialog.PairPolicyPreferLocal),
                Loc.T(StringKeys.Dialog.PairPolicyPreferRemote),
            },
            SelectedIndex = 0,
        };

        var policyLabel = new TextBlock { Text = Loc.T(StringKeys.Dialog.PairPolicyLabel), FontWeight = Avalonia.Media.FontWeight.Bold };

        // Only meaningful for a one-way pair (SyncPair.MirrorDeletes) — a two-way pair already
        // tracks deletions through its baseline, so there is no "extra file at the destination"
        // for this to opt out of. Checked by default: today's only behavior before this existed
        // was a strict mirror, and every new pair should keep that unless asked otherwise.
        var mirrorDeletesCheckBox = new CheckBox { Content = Loc.T(StringKeys.Dialog.PairMirrorDeletes), IsChecked = true };

        // The conflict policy is only ever consulted in two-way mode — a one-way mirror's source
        // side wins by definition, so showing the choice there would imply a decision that
        // doesn't exist. "Delete extra files" is the opposite: it only means something for a
        // one-way pair, so it's hidden exactly when the policy picker is shown.
        void SyncPolicyVisibility()
        {
            var isTwoWay = directionBox.SelectedIndex == 2;
            policyBox.IsVisible = isTwoWay;
            policyLabel.IsVisible = isTwoWay;
            mirrorDeletesCheckBox.IsVisible = !isTwoWay;
        }

        directionBox.SelectionChanged += (_, _) => SyncPolicyVisibility();
        SyncPolicyVisibility();

        browseButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Loc.T(StringKeys.Picker.LocalSyncFolderTitle),
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                localBox.Text = folders[0].Path.LocalPath;
            }
        };

        var addButton = new Button { Content = Loc.T(StringKeys.Common.Add), IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 80 };

        var formPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = Loc.T(StringKeys.Dialog.PairRemotePathLabel), FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { remoteBox, remoteBrowseButton }
                },
                new TextBlock { Text = Loc.T(StringKeys.Dialog.PairLocalFolderLabel), FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { localBox, browseButton }
                },
                new TextBlock { Text = Loc.T(StringKeys.Dialog.PairDirectionLabel), FontWeight = Avalonia.Media.FontWeight.Bold },
                directionBox,
                policyLabel,
                policyBox,
                mirrorDeletesCheckBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { addButton, cancelButton }
                }
            }
        };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.PairAddTitle),
            Width = 480,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = formPanel,
        };

        remoteBrowseButton.Click += (_, _) =>
            ShowRemoteFolderBrowser(dialog, syncPanel, remoteRootPath, formPanel, chosenPath =>
            {
                remoteBox.Text = chosenPath;
            });

        NewSyncPairRequest? result = null;

        addButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(remoteBox.Text) && !string.IsNullOrWhiteSpace(localBox.Text))
            {
                var direction = directionBox.SelectedIndex switch
                {
                    1 => SyncDirection.LocalToRemote,
                    2 => SyncDirection.TwoWay,
                    _ => SyncDirection.RemoteToLocal,
                };

                var policy = policyBox.SelectedIndex switch
                {
                    1 => ConflictPolicy.KeepBoth,
                    2 => ConflictPolicy.PreferLocal,
                    3 => ConflictPolicy.PreferRemote,
                    _ => ConflictPolicy.Ask,
                };

                result = new NewSyncPairRequest(remoteBox.Text.Trim(), localBox.Text.Trim(), direction, policy, mirrorDeletesCheckBox.IsChecked ?? true);
            }

            dialog.Close();
        };

        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    /// <summary>
    /// Lets an existing pair's direction/conflict policy change without recreating it. Remote/local
    /// paths aren't editable here — changing those already has a working path (remove, then add a
    /// new pair) and would need re-validating against every other pair, which this flow never does.
    /// </summary>
    private async Task<EditSyncPairRequest?> PromptForEditPairAsync(SyncPairViewModel pair)
    {
        var directionBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                Loc.T(StringKeys.Dialog.PairDirectionDownload),
                Loc.T(StringKeys.Dialog.PairDirectionUpload),
                Loc.T(StringKeys.Dialog.PairDirectionTwoWay),
            },
            SelectedIndex = pair.Direction switch
            {
                SyncDirection.LocalToRemote => 1,
                SyncDirection.TwoWay => 2,
                _ => 0,
            },
        };

        var policyBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                Loc.T(StringKeys.Dialog.PairPolicyAsk),
                Loc.T(StringKeys.Dialog.PairPolicyKeepBoth),
                Loc.T(StringKeys.Dialog.PairPolicyPreferLocal),
                Loc.T(StringKeys.Dialog.PairPolicyPreferRemote),
            },
            SelectedIndex = pair.ConflictPolicy switch
            {
                ConflictPolicy.KeepBoth => 1,
                ConflictPolicy.PreferLocal => 2,
                ConflictPolicy.PreferRemote => 3,
                _ => 0,
            },
        };

        var policyLabel = new TextBlock { Text = Loc.T(StringKeys.Dialog.PairPolicyLabel), FontWeight = Avalonia.Media.FontWeight.Bold };

        var mirrorDeletesCheckBox = new CheckBox { Content = Loc.T(StringKeys.Dialog.PairMirrorDeletes), IsChecked = pair.MirrorDeletes };

        void SyncPolicyVisibility()
        {
            var isTwoWay = directionBox.SelectedIndex == 2;
            policyBox.IsVisible = isTwoWay;
            policyLabel.IsVisible = isTwoWay;
            mirrorDeletesCheckBox.IsVisible = !isTwoWay;
        }

        directionBox.SelectionChanged += (_, _) => SyncPolicyVisibility();
        SyncPolicyVisibility();

        var saveButton = new Button { Content = Loc.T(StringKeys.Common.Save), IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 80 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.PairEditTitle),
            Width = 440,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = pair.RemotePath, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = pair.LocalPath, Opacity = 0.7 },
                    new TextBlock { Text = Loc.T(StringKeys.Dialog.PairDirectionLabel), FontWeight = Avalonia.Media.FontWeight.Bold },
                    directionBox,
                    policyLabel,
                    policyBox,
                    mirrorDeletesCheckBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { saveButton, cancelButton }
                    }
                }
            },
        };

        EditSyncPairRequest? result = null;

        saveButton.Click += (_, _) =>
        {
            var direction = directionBox.SelectedIndex switch
            {
                1 => SyncDirection.LocalToRemote,
                2 => SyncDirection.TwoWay,
                _ => SyncDirection.RemoteToLocal,
            };

            var policy = policyBox.SelectedIndex switch
            {
                1 => ConflictPolicy.KeepBoth,
                2 => ConflictPolicy.PreferLocal,
                3 => ConflictPolicy.PreferRemote,
                _ => ConflictPolicy.Ask,
            };

            result = new EditSyncPairRequest(direction, policy, mirrorDeletesCheckBox.IsChecked ?? true);
            dialog.Close();
        };

        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    /// <summary>
    /// Swaps the "Add sync pair" dialog's own content for a click-through folder browser — the
    /// remote-folder picker used to be a second window stacked on top of the add-pair dialog;
    /// this keeps it to one modal with an internal "page". "Back" restores the form instead of
    /// closing the dialog. Only existing folders can be reached this way — there's no
    /// "create folder" affordance here, unlike the local picker which can point at a
    /// not-yet-existing directory.
    /// </summary>
    private void ShowRemoteFolderBrowser(Window dialog, SyncPanelViewModel syncPanel, string startPath, Control formPanel, Action<string> onSelected)
    {
        var getChildren = syncPanel.GetRemoteFolderChildren;
        if (getChildren is null)
        {
            return;
        }

        var currentPath = startPath;
        var pathHistory = new Stack<string>();

        var pathText = new TextBlock { FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var upButton = new Button { Content = Loc.T(StringKeys.Dialog.RemoteBrowserUp), IsEnabled = false };
        var statusText = new TextBlock { Opacity = 0.7, IsVisible = false };
        var itemsPanel = new StackPanel { Spacing = 4 };
        var selectButton = new Button { Content = Loc.T(StringKeys.Dialog.RemoteBrowserSelect), IsDefault = true, Width = 200 };
        var backButton = new Button { Content = Loc.T(StringKeys.Dialog.RemoteBrowserBack), IsCancel = true, Width = 90 };

        var browsePanel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = Loc.T(StringKeys.Dialog.RemoteBrowserTitle), FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { upButton, pathText }
                },
                new ScrollViewer { Height = 300, Content = itemsPanel },
                statusText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { selectButton, backButton }
                }
            }
        };

        async Task LoadAsync()
        {
            pathText.Text = currentPath;
            upButton.IsEnabled = pathHistory.Count > 0;
            itemsPanel.Children.Clear();
            statusText.IsVisible = true;
            statusText.Text = Loc.T(StringKeys.Common.Loading);

            try
            {
                var children = await getChildren(currentPath, CancellationToken.None);
                var folders = children
                    .Where(item => item.IsFolder)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                statusText.IsVisible = folders.Count == 0;
                statusText.Text = Loc.T(StringKeys.Dialog.RemoteBrowserEmpty);

                foreach (var folder in folders)
                {
                    var childPath = folder.Path;
                    var folderButton = new Button
                    {
                        Content = Loc.F(StringKeys.Dialog.RemoteBrowserFolder, folder.Name),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                    };
                    folderButton.Click += async (_, _) =>
                    {
                        pathHistory.Push(currentPath);
                        currentPath = childPath;
                        await LoadAsync();
                    };
                    itemsPanel.Children.Add(folderButton);
                }
            }
            catch (Exception ex)
            {
                statusText.IsVisible = true;
                statusText.Text = Loc.F(StringKeys.Dialog.RemoteBrowserError, ex.Message);
            }
        }

        upButton.Click += async (_, _) =>
        {
            if (pathHistory.Count == 0)
            {
                return;
            }

            currentPath = pathHistory.Pop();
            await LoadAsync();
        };

        selectButton.Click += (_, _) =>
        {
            onSelected(currentPath);
            dialog.Content = formPanel;
        };
        backButton.Click += (_, _) => dialog.Content = formPanel;

        dialog.Content = browsePanel;
        _ = LoadAsync();
    }

    private async Task<bool> ShowPreviewAsync(SyncPlan plan, IReadOnlyList<string> warnings)
    {
        var stats = plan.Stats;
        // Each count is its own clause, joined here. The three summary lines used to be single
        // strings with "archivo(s)"/"carpeta(s)" spliced in — a Spanish-specific plural hack, and
        // one that cannot agree correctly anyway when a sentence carries two different counts
        // (docs/PLAN-I18N.md §6.3). Two clauses, two plural lookups, one line on screen.
        static string TwoClauses(string first, string second) => first + ", " + second;

        var lines = new List<string>
        {
            TwoClauses(
                Loc.Plural(StringKeys.Dialog.PreviewDownloadFiles, stats.FilesToDownload, ByteSize.Format(stats.BytesToDownload)),
                Loc.Plural(StringKeys.Dialog.PreviewDownloadFolders, stats.FoldersToCreateLocally)),
            TwoClauses(
                Loc.Plural(StringKeys.Dialog.PreviewUploadFiles, stats.FilesToUpload, ByteSize.Format(stats.BytesToUpload)),
                Loc.Plural(StringKeys.Dialog.PreviewUploadFolders, stats.FoldersToCreateRemotely)),
            TwoClauses(
                Loc.Plural(StringKeys.Dialog.PreviewTrashLocal, stats.ToDeleteLocal),
                Loc.Plural(StringKeys.Dialog.PreviewTrashRemote, stats.ToTrashRemote)),
        };

        if (stats.FilesToMoveLocally > 0)
        {
            lines.Add(Loc.Plural(StringKeys.Dialog.PreviewMovedLocally, stats.FilesToMoveLocally));
        }

        if (stats.FilesToMoveRemotely > 0)
        {
            lines.Add(Loc.Plural(StringKeys.Dialog.PreviewMovedRemotely, stats.FilesToMoveRemotely));
        }

        if (stats.Conflicts > 0)
        {
            lines.Add(Loc.Plural(StringKeys.Dialog.PreviewConflicts, stats.Conflicts));
        }

        foreach (var warning in warnings)
        {
            lines.Add(Loc.F(StringKeys.Dialog.PreviewWarning, warning));
        }

        var summary = string.Join("\n", lines);

        var actionLines = plan.Actions.Take(50).Select(a => Loc.F(StringKeys.Dialog.PreviewAction, a.Operation, a.RelativePath)).ToList();
        if (plan.Actions.Count > 50)
        {
            actionLines.Add(Loc.Plural(StringKeys.Common.More, plan.Actions.Count - 50).TrimStart('\n'));
        }

        // A plan with no actions but with conflicts isn't "up to date" — under the Ask policy that
        // is precisely the state that needs the user, so don't tell them everything is fine.
        var actionsText = actionLines.Count > 0
            ? string.Join("\n", actionLines)
            : stats.Conflicts > 0
                ? Loc.T(StringKeys.Dialog.PreviewNoActionsConflicts)
                : Loc.T(StringKeys.Dialog.PreviewNoActionsUpToDate);

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.PreviewTitle),
            Width = 520,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = summary, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new ScrollViewer
                    {
                        Height = 230,
                        Content = new TextBlock { Text = actionsText, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = Loc.T(StringKeys.Menu.SyncNow), IsDefault = true, Width = 160, IsEnabled = plan.Actions.Count > 0 },
                            new Button { Content = Loc.T(StringKeys.Common.Close), IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        var result = false;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var runButton = (Button)buttonsPanel.Children[0];
        var closeButton = (Button)buttonsPanel.Children[1];

        runButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        closeButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    /// <summary>
    /// §5.6's per-file resolution panel. Every file starts on "Decide later" rather than a default
    /// action: these are the cases the engine refused to guess at, so the dialog must not guess
    /// either. Closing without choosing anything therefore changes nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, ConflictResolution>> ShowConflictsAsync(IReadOnlyList<QueuedSyncAction> conflicts)
    {
        var chosen = new Dictionary<long, ConflictResolution>();
        var rows = new StackPanel { Spacing = 10 };

        foreach (var conflict in conflicts)
        {
            var selector = new ComboBox
            {
                Width = 260,
                ItemsSource = new[]
                {
                    Loc.T(StringKeys.Dialog.ConflictsChoiceLater),
                    Loc.T(StringKeys.Dialog.ConflictsChoiceKeepBoth),
                    Loc.T(StringKeys.Dialog.ConflictsChoiceKeepLocal),
                    Loc.T(StringKeys.Dialog.ConflictsChoiceKeepRemote),
                },
                SelectedIndex = 0,
            };

            var id = conflict.Id;
            selector.SelectionChanged += (_, _) =>
            {
                switch (selector.SelectedIndex)
                {
                    case 1: chosen[id] = ConflictResolution.KeepBoth; break;
                    case 2: chosen[id] = ConflictResolution.KeepLocal; break;
                    case 3: chosen[id] = ConflictResolution.KeepRemote; break;
                    default: chosen.Remove(id); break;
                }
            };

            rows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = conflict.RelativePath, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = DescribeReason(conflict.LastError), Opacity = 0.7, FontSize = 12 },
                    selector,
                }
            });
        }

        var applyButton = new Button { Content = Loc.T(StringKeys.Common.Apply), IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.Plural(StringKeys.Dialog.ConflictsTitle, conflicts.Count),
            Width = 560,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T(StringKeys.Dialog.ConflictsIntro),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    new ScrollViewer { Height = 280, Content = rows },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { applyButton, cancelButton }
                    }
                }
            }
        };

        var apply = false;
        applyButton.Click += (_, _) =>
        {
            apply = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return apply ? chosen : new Dictionary<long, ConflictResolution>();
    }

    /// <summary>
    /// The failures view (docs/PLAN-UX-ROUND-2.md §6). Same shape as
    /// <see cref="ShowConflictsAsync"/>, because it is the same kind of decision: here is what
    /// happened per file, choose per file. The pair row previously showed only "N acción(es)
    /// fallaron" and a blind retry-everything button, while the per-action reason sat unread in
    /// the queue.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, SyncFailureDecision>> ShowFailuresAsync(IReadOnlyList<SyncFailureViewModel> failures)
    {
        var chosen = new Dictionary<long, SyncFailureDecision>();
        var rows = new StackPanel { Spacing = 12 };

        foreach (var failure in failures)
        {
            var selector = new ComboBox
            {
                Width = 260,
                ItemsSource = new[]
                {
                    Loc.T(StringKeys.Dialog.FailuresChoiceLeave),
                    Loc.T(StringKeys.Dialog.FailuresChoiceRetry),
                    Loc.T(StringKeys.Dialog.FailuresChoiceDiscard),
                },
                SelectedIndex = 0,
            };

            var id = failure.Id;
            selector.SelectionChanged += (_, _) =>
            {
                switch (selector.SelectedIndex)
                {
                    case 1: chosen[id] = SyncFailureDecision.Retry; break;
                    case 2: chosen[id] = SyncFailureDecision.Discard; break;
                    default: chosen.Remove(id); break;
                }
            };

            rows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = failure.RelativePath, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = failure.Summary, Opacity = 0.7, FontSize = 12 },
                    // The provider's own words, verbatim and wrapped rather than trimmed: this is
                    // the sentence that tells the user whether it is their problem or ours.
                    new TextBlock { Text = failure.ReasonText, Opacity = 0.9, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    selector,
                }
            });
        }

        var retryAllButton = new Button { Content = Loc.T(StringKeys.Dialog.FailuresRetryAll), Width = 160 };
        var applyButton = new Button { Content = Loc.T(StringKeys.Common.Apply), IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.Plural(StringKeys.Dialog.FailuresTitle, failures.Count),
            Width = 620,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T(StringKeys.Dialog.FailuresIntro),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    new ScrollViewer { Height = 300, Content = rows },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { retryAllButton, applyButton, cancelButton }
                    }
                }
            }
        };

        var apply = false;

        // The old one-click behavior, kept: deciding file by file is the new capability, not a new
        // obligation.
        retryAllButton.Click += (_, _) =>
        {
            foreach (var failure in failures)
            {
                chosen[failure.Id] = SyncFailureDecision.Retry;
            }

            apply = true;
            dialog.Close();
        };

        applyButton.Click += (_, _) =>
        {
            apply = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return apply ? chosen : new Dictionary<long, SyncFailureDecision>();
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

    /// <summary>A read-only "Properties" info panel — "Propiedades" (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6).</summary>
    private async Task ShowPropertiesAsync(string title, IReadOnlyList<PropertyField> fields)
    {
        var children = new List<Control>
        {
            new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        };

        foreach (var field in fields)
        {
            var text = new TextBlock
            {
                Text = Loc.F(StringKeys.Dialog.PropertiesField, field.Label, field.Value),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (!field.IsCopyable)
            {
                children.Add(text);
                continue;
            }

            // Paths are the only values here anyone needs elsewhere, and the only ones long enough
            // that retyping them is not an option (docs/PLAN-UX-ROUND-2.md §12).
            var copyButton = new Button
            {
                Content = Loc.T(StringKeys.Common.Copy),
                FontSize = 11,
                Padding = new Avalonia.Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var value = field.Value;
            copyButton.Click += async (_, _) =>
            {
                await CopyToClipboardAsync(value);
                // Confirm in place: a clipboard write is otherwise completely invisible.
                copyButton.Content = Loc.T(StringKeys.Common.Copied);
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(copyButton, 1);
            row.Children.Add(text);
            row.Children.Add(copyButton);
            children.Add(row);
        }

        var okButton = new Button { Content = Loc.T(StringKeys.Common.Ok), IsDefault = true, IsCancel = true, Width = 80 };
        children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { okButton },
        });

        var contentPanel = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(20) };
        contentPanel.Children.AddRange(children);

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.PropertiesTitle),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = contentPanel,
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// A plain yes/no. "Cancel" is the default button, not "Continue": every question routed here is
    /// a warning about doing something big, so the safe answer should be the one a stray Enter picks.
    /// </summary>
    private async Task<bool> AskAsync(string question)
    {
        var yes = new Button { Content = Loc.T(StringKeys.Common.Continue), Width = 100 };
        var no = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.ConfirmTitle),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { yes, no }
                    }
                }
            }
        };

        var confirmed = false;
        yes.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return confirmed;
    }

    /// <summary>
    /// A blocking, single-button notice — for a rejection the user has to actually see, not a
    /// <c>StatusMessage</c> line that can change again (or scroll away) before anyone reads it.
    /// Mirrors <see cref="AskAsync"/>'s shape with the "Cancel" button dropped: there's nothing to
    /// decide here, only something to acknowledge (docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2 —
    /// a rejected sync-pair-direction change looked indistinguishable from a silently-failed save).
    /// </summary>
    private async Task ShowAlertAsync(string message)
    {
        var ok = new Button { Content = Loc.T(StringKeys.Common.Ok), IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.AlertTitle),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok }
                    }
                }
            }
        };

        ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }

    private static string DescribeReason(string? reason) => reason switch
    {
        nameof(ConflictReason.BothChanged) => Loc.T(StringKeys.Dialog.ConflictsReasonBothChanged),
        nameof(ConflictReason.BothAppearedDiffering) => Loc.T(StringKeys.Dialog.ConflictsReasonBothAppeared),
        nameof(ConflictReason.RemoteDeletedLocalChanged) => Loc.T(StringKeys.Dialog.ConflictsReasonRemoteDeleted),
        nameof(ConflictReason.LocalDeletedRemoteChanged) => Loc.T(StringKeys.Dialog.ConflictsReasonLocalDeleted),
        _ => Loc.T(StringKeys.Dialog.ConflictsReasonDefault),
    };

}
