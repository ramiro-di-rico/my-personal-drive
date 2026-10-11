using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

/// <summary>
/// The explorer view: the cloud and local panes side by side, the status sidebar, the activity
/// console and the preview panel. Markup, plus what concerns the layout between the panes — the
/// splitter's reset gesture and the local pane's column — and which pane has focus.
/// </summary>
public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // ExplorerColumnsGrid.ColumnDefinitions[2] is star-sized so the splitter can resize it —
        // which also means it doesn't shrink to 0 on its own just because IsVisible on its content
        // goes false, the way an Auto column (the Status sidebar's) does. Kept in sync here instead.
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLocalExplorerPanelVisible) && sender is MainWindowViewModel viewModel)
        {
            ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);
        }
    }

    /// <summary>Puts the keyboard cursor in the active pane's quick filter (Ctrl+F).</summary>
    public void FocusSearch()
    {
        if (ActivePaneIsLocal())
        {
            LocalPaneView.FocusSearch();
        }
        else
        {
            CloudPaneView.FocusSearch();
        }
    }

    /// <summary>Gives the cloud list focus so arrow keys and Enter work without a click first.</summary>
    public void FocusList() => CloudPaneView.FocusList();

    /// <summary>
    /// Which pane a keystroke belongs to. Focus decides it, and the cloud pane is the default —
    /// the local pane can be hidden entirely, so "wherever focus last was" has to fall back to the
    /// pane that is always there.
    /// </summary>
    public bool ActivePaneIsLocal()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        for (var visual = focused; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is LocalPane)
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
}
