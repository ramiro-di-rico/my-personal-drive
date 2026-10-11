using Avalonia.Controls;

namespace MyPersonalDrive.Views;

/// <summary>
/// The "a drop would land here" highlight, shared by both panes: the whole listing while a valid
/// drag is over it, and additionally the one folder row the drop would land inside. It is purely
/// visual — a class toggled from the drag handlers, which the <c>.dropTarget</c> styles in
/// <c>ExplorerStyles.axaml</c> select on. Where the drop actually goes is decided again, by the
/// same hit-testing, when it happens.
/// </summary>
internal sealed class DropHighlight
{
    private const string DropTargetClass = "dropTarget";

    private Control? _folderRow;

    /// <summary>
    /// Highlights <paramref name="listing"/>, and <paramref name="folderRow"/> when the drop would
    /// land inside that folder rather than in the pane's current folder (<c>null</c> then).
    /// </summary>
    public void Show(Control? listing, Control? folderRow)
    {
        listing?.Classes.Add(DropTargetClass);

        if (!ReferenceEquals(folderRow, _folderRow))
        {
            _folderRow?.Classes.Remove(DropTargetClass);
            _folderRow = folderRow;
            _folderRow?.Classes.Add(DropTargetClass);
        }
    }

    public void Clear(Control? listing)
    {
        listing?.Classes.Remove(DropTargetClass);
        _folderRow?.Classes.Remove(DropTargetClass);
        _folderRow = null;
    }
}
