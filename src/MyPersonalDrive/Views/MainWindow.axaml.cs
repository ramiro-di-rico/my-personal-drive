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
using static MyPersonalDrive.Views.RowGestures;

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

        // Read before anything narrows them, so compact mode has something to put back and the
        // two numbers live in exactly one place — the XAML attributes.
        _fullMinWidth = MinWidth;
        _fullMinHeight = MinHeight;
        Closing += OnClosingRememberCompactPosition;

        // Everything else on the keyboard (docs/PLAN-UX-ROUND-3.md X5). Bubble at the window, not
        // KeyBindings and not a per-control handler: a focused TextBox marks Ctrl+A, Delete and F2
        // as handled while it is doing its own editing, so a bubble-phase handler is guarded
        // against stealing them for free — which is exactly the concern that kept Ctrl+A scoped to
        // one ListBox before, and the reason the tile modes never got it (an ItemsRepeater takes no
        // focus, so it has no KeyDown of its own to hang anything on).
        KeyDown += OnWindowKeyDown;
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
            case Avalonia.Input.Key.Escape when viewModel.Preview.IsViewerVisible:
                Run(viewModel.Preview.CloseViewerCommand);
                break;

            case Avalonia.Input.Key.F5:
                Run(ActivePaneIsLocal() ? viewModel.LocalExplorer.RefreshCommand : viewModel.RefreshCommand);
                break;

            case Avalonia.Input.Key.N when control && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Run(viewModel.CreateFolderCommand);
                break;

            case Avalonia.Input.Key.F when control:
                if (ActivePaneIsLocal())
                {
                    LocalPaneView.FocusSearch();
                }
                else
                {
                    CloudSearchBox.Focus();
                }

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

    private void OnMainWindowViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLocalExplorerPanelVisible) && sender is MainWindowViewModel viewModel)
        {
            ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsCompactMode) && sender is MainWindowViewModel compactViewModel)
        {
            ApplyCompactMode(compactViewModel, compactViewModel.IsCompactMode);
        }
    }

    private readonly double _fullMinWidth;
    private readonly double _fullMinHeight;

    /// <summary>
    /// What the window chrome is currently set up for, or <c>null</c> before the first apply. The
    /// null state is what keeps the initial apply from counting as a transition: entering compact
    /// records the size to restore, and leaving it records where the strip sat, but doing either
    /// on launch would overwrite last session's values with this session's defaults.
    /// </summary>
    private bool? _compactChromeApplied;

    /// <summary>The strip's own width and its floor. Height is left to the content.</summary>
    private const double CompactWindowWidth = 420;
    private const double CompactMinWidth = 280;
    private const double CompactMinHeight = 32;

    /// <summary>
    /// Resizes, pins and places the window for the compact strip, or puts it back
    /// (docs/PLAN-COMPACT-VIEW.md). Not business logic living in code-behind: `MinWidth`,
    /// `SizeToContent`, `Topmost` and `Position` are window properties with no view-model
    /// equivalent, and the view model deliberately owns none of them.
    ///
    /// Order matters on the way in — the XAML minimums are 960x600, so `Width` has to be narrowed
    /// *after* `MinWidth` drops or the assignment is silently clamped straight back up.
    /// </summary>
    private void ApplyCompactMode(MainWindowViewModel viewModel, bool compact)
    {
        if (_compactChromeApplied == compact)
        {
            return;
        }

        var isTransition = _compactChromeApplied is not null;

        if (compact)
        {
            if (isTransition)
            {
                viewModel.SaveRestoreWindowSize(Width, Height);
            }

            MinWidth = CompactMinWidth;
            MinHeight = CompactMinHeight;
            // Height follows the strip rather than a constant: the row's own padding, font size
            // and DPI decide how tall one line of it is, and a hard-coded number would clip it on
            // any machine that disagrees.
            SizeToContent = SizeToContent.Height;
            Width = CompactWindowWidth;
            Topmost = true;

            if (viewModel.CompactWindowPosition is { } position)
            {
                Position = new PixelPoint(position.Left, position.Top);
            }
        }
        else
        {
            if (isTransition)
            {
                viewModel.SaveCompactWindowPosition(Position.X, Position.Y);
            }

            Topmost = false;
            SizeToContent = SizeToContent.Manual;
            MinWidth = _fullMinWidth;
            MinHeight = _fullMinHeight;

            if (viewModel.RestoreWindowSize is { } size)
            {
                Width = size.Width;
                Height = size.Height;
            }
        }

        _compactChromeApplied = compact;
    }

    /// <summary>
    /// The strip's position is saved when it is expanded, which never happens if the app is closed
    /// while still compact — so it is saved on the way out too.
    /// </summary>
    private void OnClosingRememberCompactPosition(object? sender, WindowClosingEventArgs e)
    {
        if (_compactChromeApplied == true && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveCompactWindowPosition(Position.X, Position.Y);
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
        viewModel.RequestShowPropertiesAsync = (title, fields, syncStatuses) => Dialogs.PropertiesDialog.ShowAsync(this, title, fields, syncStatuses);
        viewModel.RequestPdfMergeAsync = (names, suggested) => Dialogs.PdfMergeDialog.ShowAsync(this, names, suggested);
        viewModel.RequestMoveTargetAsync = (what, startPath) => Dialogs.RemoteFolderPickerDialog.ShowAsync(
            this,
            viewModel.ListRemoteFolderAsync,
            Localizer.Instance.F(StringKeys.Dialog.MoveTargetPrompt, what),
            startPath);

        viewModel.LocalExplorer.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(this, question);
        viewModel.LocalExplorer.RequestRenameAsync = PromptForRenameAsync;
        viewModel.LocalExplorer.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.LocalExplorer.RequestShowPropertiesAsync = (title, fields, syncStatuses) => Dialogs.PropertiesDialog.ShowAsync(this, title, fields, syncStatuses);
        viewModel.LocalExplorer.RequestPdfMergeAsync = (names, suggested) => Dialogs.PdfMergeDialog.ShowAsync(this, names, suggested);


        // ExplorerColumnsGrid.ColumnDefinitions[2] is star-sized so the splitter can resize it —
        // which also means it doesn't shrink to 0 on its own just because IsVisible on its content
        // goes false, the way an Auto column (the Status sidebar's) does. Kept in sync here instead.
        viewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        viewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);
        ApplyCompactMode(viewModel, viewModel.IsCompactMode);

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
                CloudListingsView.FocusList();
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
