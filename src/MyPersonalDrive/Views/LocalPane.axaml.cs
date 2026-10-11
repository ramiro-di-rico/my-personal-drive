using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Local;
using static MyPersonalDrive.Views.RowGestures;

namespace MyPersonalDrive.Views;

/// <summary>
/// The local file pane of the explorer. Markup, plus what needs the visual tree: row gestures
/// (activate, multi-select, start a drag) and the drop target that receives cloud items.
/// </summary>
public partial class LocalPane : UserControl
{
    private static Localizer Loc => Localizer.Instance;

    private readonly RowDragSource<LocalNodeViewModel> _rowDrag;

    public LocalPane()
    {
        InitializeComponent();
        _rowDrag = new RowDragSource<LocalNodeViewModel>(CreateTransfer, ex => ReportDragFailure(DataContext, ex));

        // Tunnel, not the plain bubble subscription a XAML PointerPressed/PointerMoved attribute
        // on the row Button itself would give: ButtonBase marks these events Handled as part of
        // its own click tracking (RoutingStrategies.Bubble), so a same-strategy handler attached
        // on the Button never saw them at all — drag-and-drop never started, full stop. Attaching
        // at the ListBox with Tunnel runs this before that happens (docs/INTERFACE_IMPROVEMENT_PLAN.md
        // Task 5).
        LocalListing.AddHandler(InputElement.PointerPressedEvent, OnLocalRowPointerPressed, RoutingStrategies.Tunnel);
        LocalListing.AddHandler(InputElement.PointerMovedEvent, _rowDrag.OnPointerMoved, RoutingStrategies.Tunnel);

        // Double click opens. Bubble, and only Bubble: DoubleTappedEvent is registered with
        // RoutingStrategies.Bubble alone, so a handler added for Tunnel is attached to a phase the
        // event never routes and is simply never called. handledEventsToo, because the row roots
        // are Buttons and the gesture reaches them on the way up.
        LocalListing.AddHandler(InputElement.DoubleTappedEvent, OnLocalRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Puts the keyboard cursor in this pane's quick filter (Ctrl+F).</summary>
    public void FocusSearch() => LocalSearchBox.Focus();

    private void OnLocalRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (NodeUnder<LocalNodeViewModel>(e.Source) is { } node)
        {
            node.ActivateCommand.Execute(null);
            e.Handled = true;
        }
    }

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

        _rowDrag.Arm(e, node);
    }

    /// <summary>
    /// Both files and folders can be dragged — <c>filesystem upload</c> already accepts folder
    /// paths (its <c>-d</c>/folder-conflict-strategy flag exists for exactly this), unlike the
    /// row's own manual <c>DownloadCommand</c>, which is folder-restricted for an unrelated reason
    /// (no UI need for it before now, not a CLI limitation — see docs/ARCHITECTURE.md §9 item 11).
    /// </summary>
    private static DataTransfer CreateTransfer(LocalNodeViewModel node)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(LocalPathsDataFormat, new[] { node.Item.Path }));
        return transfer;
    }

    private readonly DropHighlight _dropHighlight = new();

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
        var hoveredRow = e.Source is Visual visual ? visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) : null;
        _dropHighlight.Show(listBox, hoveredRow?.DataContext is LocalNodeViewModel { IsFolder: true } ? hoveredRow : null);

        var targetPath = ResolveLocalDropTargetPath(e, viewModel);
        LocalDropOverlayText.Text = Loc.F(StringKeys.Drop.DownloadTo, DisplayNameForDropTarget(targetPath, viewModel.LocalExplorer.CurrentPath));
        LocalDropOverlay.IsVisible = true;
    }

    private void OnLocalListingDragLeave(object? sender, DragEventArgs e) => ClearLocalDropHighlight(sender as ListBox);

    private void ClearLocalDropHighlight(ListBox? listBox)
    {
        _dropHighlight.Clear(listBox);
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
}
