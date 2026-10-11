using Avalonia.Controls;

namespace MyPersonalDrive.Views;

/// <summary>
/// The cloud file pane of the explorer: header, search and sort row, batch action bar, type
/// filters and the listing. It composes the pieces; the window reaches it only to put the
/// keyboard cursor somewhere.
/// </summary>
public partial class CloudPane : UserControl
{
    public CloudPane()
    {
        InitializeComponent();
    }

    /// <summary>Puts the keyboard cursor in this pane's quick filter (Ctrl+F).</summary>
    public void FocusSearch() => CloudSearchBox.Focus();

    /// <summary>Gives the list focus so arrow keys and Enter work without a click first.</summary>
    public void FocusList() => CloudListingsView.FocusList();
}
