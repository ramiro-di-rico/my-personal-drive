using Avalonia.Controls;

namespace MyPersonalDrive.Views;

/// <summary>
/// The sync pair list, as its own view rather than the last section of the settings scroll
/// (docs/PLAN-UX-ROUND-2.md §5). Pure markup: the dialogs the panel needs (conflict resolution,
/// pair creation/editing) are still driven from <see cref="MainWindow"/>'s code-behind through
/// <c>SyncPanelViewModel</c>'s callbacks, so nothing moved with it.
/// </summary>
public partial class SyncPanelView : UserControl
{
    public SyncPanelView()
    {
        InitializeComponent();
    }
}
