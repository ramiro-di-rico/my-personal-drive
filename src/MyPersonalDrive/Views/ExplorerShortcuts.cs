using Avalonia.Input;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

/// <summary>
/// The explorer's keyboard shortcuts. Which pane a key belongs to is the explorer's to say; what
/// the key then does is a call on the view model.
/// </summary>
internal static class ExplorerShortcuts
{
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
    public static void Handle(Avalonia.Input.KeyEventArgs e, MainWindowViewModel viewModel, ExplorerView explorer)
    {
        if (e.Handled)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        switch (e.Key)
        {
            // Closes the viewer panel and nothing else: unguarded, Escape would also be the way out
            // of a dialog, and those are separate windows with their own IsCancel buttons.
            case Avalonia.Input.Key.Escape when viewModel.Preview.IsViewerVisible:
                Run(viewModel.Preview.CloseViewerCommand);
                break;

            case Avalonia.Input.Key.F5:
                Run(explorer.ActivePaneIsLocal() ? viewModel.LocalExplorer.RefreshCommand : viewModel.RefreshCommand);
                break;

            case Avalonia.Input.Key.N when control && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Run(viewModel.CreateFolderCommand);
                break;

            case Avalonia.Input.Key.F when control:
                explorer.FocusSearch();
                e.Handled = true;
                break;

            case Avalonia.Input.Key.A when control:
                Run(explorer.ActivePaneIsLocal() ? viewModel.LocalExplorer.SelectAllCommand : viewModel.SelectAllRowsCommand);
                break;

            // Back a folder. Backspace is the file-manager idiom; Alt+Left is the browser one, and
            // both are free here because neither pane hosts an editable surface that wants them.
            case Avalonia.Input.Key.Back:
            case Avalonia.Input.Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                Run(explorer.ActivePaneIsLocal() ? viewModel.LocalExplorer.BackCommand : viewModel.BackCommand);
                break;

            // F2 renames exactly one row. With several marked there is no single name to edit, and
            // silently renaming the first would be worse than doing nothing.
            case Avalonia.Input.Key.F2 when explorer.ActivePaneIsLocal():
                Run(SingleSelected(viewModel.LocalExplorer.Items, node => node.IsSelected)?.RenameCommand);
                break;

            case Avalonia.Input.Key.F2:
                Run(SingleSelected(viewModel.RootItems, node => node.IsSelected)?.RenameCommand);
                break;

            // Delete acts on the whole selection, and goes through the same command the buttons
            // use — so it inherits their confirmation prompt rather than deleting on a keypress.
            case Avalonia.Input.Key.Delete when explorer.ActivePaneIsLocal():
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
}
