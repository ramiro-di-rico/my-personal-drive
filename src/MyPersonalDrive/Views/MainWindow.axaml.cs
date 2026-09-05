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
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Local;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views;

public partial class MainWindow : Window
{
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
        node.RowCommand.Execute(null);
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

    /// <summary>
    /// Collapses/restores the local pane's star-sized column directly, since toggling `IsVisible`
    /// on its content (done via binding in XAML) has no effect on a `*` column's own width — unlike
    /// an `Auto` column, which already shrinks to 0 the moment its content stops participating in
    /// layout. Restoring always goes back to an even split rather than whatever ratio the user last
    /// dragged to; remembering that ratio isn't worth the extra state for a show/hide toggle.
    /// </summary>
    private void ApplyLocalExplorerPanelColumnWidth(bool visible)
        => ExplorerColumnsGrid.ColumnDefinitions[2].Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

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
        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (row?.DataContext is not LocalNodeViewModel node || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
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
                vm.StatusMessage = $"Error al iniciar el arrastre: {ex.Message}";
            }
        }
    }

    private ListBoxItem? _cloudHighlightedDropRow;

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
        var listBox = sender as ListBox;
        var hasFormat = e.DataTransfer.Contains(LocalPathsDataFormat);
        if (!hasFormat || DataContext is not MainWindowViewModel viewModel)
        {
            e.DragEffects = DragDropEffects.None;
            ClearCloudDropHighlight(listBox);
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        listBox?.Classes.Add("dropTarget");

        var hoveredRow = e.Source is Visual visual ? visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) : null;
        var targetsAFolderRow = hoveredRow?.DataContext is DriveNodeViewModel { IsFolder: true };
        if (!ReferenceEquals(hoveredRow, _cloudHighlightedDropRow) || !targetsAFolderRow)
        {
            _cloudHighlightedDropRow?.Classes.Remove("dropTarget");
            _cloudHighlightedDropRow = targetsAFolderRow ? hoveredRow : null;
            _cloudHighlightedDropRow?.Classes.Add("dropTarget");
        }

        var targetPath = ResolveCloudDropTargetPath(e, viewModel);
        CloudDropOverlayText.Text = $"+ Subir a {DisplayNameForDropTarget(targetPath, viewModel.CurrentPath)}";
        CloudDropOverlay.IsVisible = true;
    }

    private void OnCloudListingDragLeave(object? sender, DragEventArgs e) => ClearCloudDropHighlight(sender as ListBox);

    private void ClearCloudDropHighlight(ListBox? listBox)
    {
        listBox?.Classes.Remove("dropTarget");
        _cloudHighlightedDropRow?.Classes.Remove("dropTarget");
        _cloudHighlightedDropRow = null;
        CloudDropOverlay.IsVisible = false;
    }

    private async void OnCloudListingDrop(object? sender, DragEventArgs e)
    {
        ClearCloudDropHighlight(sender as ListBox);

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

    /// <summary>"la carpeta actual" for the folder already open, otherwise that folder's own name.</summary>
    private static string DisplayNameForDropTarget(string targetPath, string currentPath)
    {
        if (string.Equals(targetPath, currentPath, StringComparison.Ordinal))
        {
            return "la carpeta actual";
        }

        var trimmed = targetPath.TrimEnd('/');
        var lastSeparator = trimmed.LastIndexOf('/');
        return lastSeparator >= 0 && lastSeparator < trimmed.Length - 1 ? trimmed[(lastSeparator + 1)..] : trimmed;
    }

    /// <summary>The folder row under the drop point, if any — otherwise the currently browsed folder.</summary>
    private static string ResolveCloudDropTargetPath(DragEventArgs e, MainWindowViewModel viewModel)
    {
        if (e.Source is Visual visual)
        {
            var listBoxItem = visual.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (listBoxItem?.DataContext is DriveNodeViewModel { IsFolder: true } node)
            {
                return node.Path;
            }
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
        // Attached at the ListBox (see the constructor), not the row itself — see the matching
        // comment on OnLocalRowPointerPressed.
        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (row?.DataContext is not DriveNodeViewModel node || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
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
                vm.StatusMessage = $"Error al iniciar el arrastre: {ex.Message}";
            }
        }
    }

    private ListBoxItem? _localHighlightedDropRow;

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
        LocalDropOverlayText.Text = $"↓ Descargar a {DisplayNameForDropTarget(targetPath, viewModel.LocalExplorer.CurrentPath)}";
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
            Title = "Seleccioná el ejecutable de proton-drive",
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
            Title = "Seleccioná la carpeta de sincronización por defecto",
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
            Title = "Seleccioná los archivos a subir",
            AllowMultiple = true
        });

        return files.Select(file => file.Path.LocalPath).ToList();
    }

    private async Task<string?> PickDownloadFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Seleccioná la carpeta de descarga",
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
            Title = "Renombrar elemento",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Ingresá el nuevo nombre para '{currentName}':", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Renombrar", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancelar", IsCancel = true, Width = 80 }
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
            PlaceholderText = "Nombre de la carpeta nueva",
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = "Crear carpeta",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "Ingresá el nombre de la carpeta nueva:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Crear", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancelar", IsCancel = true, Width = 80 }
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
            PlaceholderText = "Dejalo vacío para usar el nombre original",
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = "Crear una copia",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Nombre nuevo (opcional) para la copia de '{currentName}':", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Copiar", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancelar", IsCancel = true, Width = 80 }
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
            filesList += $"\n... y {conflictingFiles.Count - 10} más";
        }

        var dialog = new Window
        {
            Title = "Conflicto al subir",
            Width = 450,
            Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "Estos archivos ya existen en la nube:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = filesList, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "¿Qué querés hacer?", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Vertical,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Children =
                        {
                            new Button { Content = "Conservar ambos (renombrar los nuevos)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.KeepBoth },
                            new Button { Content = "Reemplazar (sobrescribir los existentes)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Replace },
                            new Button { Content = "Omitir (no subir los archivos en conflicto)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Skip },
                            new Button { Content = "Cancelar", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.None }
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
            Title = "Guardar la actividad de la CLI",
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
        var remoteBrowseButton = new Button { Content = "Explorar", IsVisible = syncPanel.GetRemoteFolderChildren is not null };
        var localBox = new TextBox { Width = 280, IsReadOnly = true, PlaceholderText = "Elegí una carpeta local...", Text = prefill?.LocalPath };
        var browseButton = new Button { Content = "Explorar" };

        // RemoteToLocal stays first, and therefore the default: it's the only direction that
        // cannot destroy anything in the cloud (docs/PLAN-LOCAL-SYNC.md §15).
        var directionBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                "Solo descargar  (Remoto → Local)",
                "Solo subir  (Local → Remoto)",
                "Bidireccional  (Remoto ↔ Local)",
            },
            SelectedIndex = 0,
        };

        var policyBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                "Preguntarme  (dejar el conflicto pendiente y decidir después)",
                "Conservar ambos  (nunca se pierde ninguna versión)",
                "Preferir la local",
                "Preferir la remota",
            },
            SelectedIndex = 0,
        };

        var policyLabel = new TextBlock { Text = "Cuando cambian los dos lados:", FontWeight = Avalonia.Media.FontWeight.Bold };

        // Only meaningful for a one-way pair (SyncPair.MirrorDeletes) — a two-way pair already
        // tracks deletions through its baseline, so there is no "extra file at the destination"
        // for this to opt out of. Checked by default: today's only behavior before this existed
        // was a strict mirror, and every new pair should keep that unless asked otherwise.
        var mirrorDeletesCheckBox = new CheckBox { Content = "Borrar en el destino los archivos que ya no existen en el origen", IsChecked = true };

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
                Title = "Seleccioná la carpeta local a sincronizar",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                localBox.Text = folders[0].Path.LocalPath;
            }
        };

        var addButton = new Button { Content = "Agregar", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancelar", IsCancel = true, Width = 80 };

        var formPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "Ruta de la carpeta remota:", FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { remoteBox, remoteBrowseButton }
                },
                new TextBlock { Text = "Carpeta local:", FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { localBox, browseButton }
                },
                new TextBlock { Text = "Dirección:", FontWeight = Avalonia.Media.FontWeight.Bold },
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
            Title = "Agregar par de sincronización",
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
                "Solo descargar  (Remoto → Local)",
                "Solo subir  (Local → Remoto)",
                "Bidireccional  (Remoto ↔ Local)",
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
                "Preguntarme  (dejar el conflicto pendiente y decidir después)",
                "Conservar ambos  (nunca se pierde ninguna versión)",
                "Preferir la local",
                "Preferir la remota",
            },
            SelectedIndex = pair.ConflictPolicy switch
            {
                ConflictPolicy.KeepBoth => 1,
                ConflictPolicy.PreferLocal => 2,
                ConflictPolicy.PreferRemote => 3,
                _ => 0,
            },
        };

        var policyLabel = new TextBlock { Text = "Cuando cambian los dos lados:", FontWeight = Avalonia.Media.FontWeight.Bold };

        var mirrorDeletesCheckBox = new CheckBox { Content = "Borrar en el destino los archivos que ya no existen en el origen", IsChecked = pair.MirrorDeletes };

        void SyncPolicyVisibility()
        {
            var isTwoWay = directionBox.SelectedIndex == 2;
            policyBox.IsVisible = isTwoWay;
            policyLabel.IsVisible = isTwoWay;
            mirrorDeletesCheckBox.IsVisible = !isTwoWay;
        }

        directionBox.SelectionChanged += (_, _) => SyncPolicyVisibility();
        SyncPolicyVisibility();

        var saveButton = new Button { Content = "Guardar", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancelar", IsCancel = true, Width = 80 };

        var dialog = new Window
        {
            Title = "Editar par de sincronización",
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
                    new TextBlock { Text = "Dirección:", FontWeight = Avalonia.Media.FontWeight.Bold },
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
        var upButton = new Button { Content = "⬆ Subir un nivel", IsEnabled = false };
        var statusText = new TextBlock { Opacity = 0.7, IsVisible = false };
        var itemsPanel = new StackPanel { Spacing = 4 };
        var selectButton = new Button { Content = "Elegir esta carpeta", IsDefault = true, Width = 200 };
        var backButton = new Button { Content = "◀ Volver", IsCancel = true, Width = 90 };

        var browsePanel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "Elegí una carpeta remota", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold },
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
            statusText.Text = "Cargando...";

            try
            {
                var children = await getChildren(currentPath, CancellationToken.None);
                var folders = children
                    .Where(item => item.IsFolder)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                statusText.IsVisible = folders.Count == 0;
                statusText.Text = "(no hay subcarpetas acá)";

                foreach (var folder in folders)
                {
                    var childPath = folder.Path;
                    var folderButton = new Button
                    {
                        Content = $"📁 {folder.Name}",
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
                statusText.Text = $"No se pudo listar esta carpeta: {ex.Message}";
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
        var lines = new List<string>
        {
            $"↓ {stats.FilesToDownload} archivo(s) a descargar ({FormatBytes(stats.BytesToDownload)}), {stats.FoldersToCreateLocally} carpeta(s) a crear localmente.",
            $"↑ {stats.FilesToUpload} archivo(s) a subir ({FormatBytes(stats.BytesToUpload)}), {stats.FoldersToCreateRemotely} carpeta(s) a crear en el remoto.",
            $"🗑 {stats.ToDeleteLocal} elemento(s) local(es) a la papelera local, {stats.ToTrashRemote} elemento(s) remoto(s) a la papelera de Proton.",
        };

        if (stats.FilesToMoveLocally > 0)
        {
            lines.Add($"↔ {stats.FilesToMoveLocally} archivo(s) movido(s) localmente para seguir a Proton Drive — no hace falta volver a descargarlos.");
        }

        if (stats.FilesToMoveRemotely > 0)
        {
            lines.Add($"↔ {stats.FilesToMoveRemotely} archivo(s) movido(s) en Proton Drive para seguir a esta máquina — no hace falta volver a subirlos.");
        }

        if (stats.Conflicts > 0)
        {
            lines.Add($"⚠ {stats.Conflicts} conflicto(s) — cambiaron los dos lados.");
        }

        foreach (var warning in warnings)
        {
            lines.Add($"⛔ {warning}");
        }

        var summary = string.Join("\n", lines);

        var actionLines = plan.Actions.Take(50).Select(a => $"{a.Operation}  {a.RelativePath}").ToList();
        if (plan.Actions.Count > 50)
        {
            actionLines.Add($"... y {plan.Actions.Count - 50} más");
        }

        // A plan with no actions but with conflicts isn't "up to date" — under the Ask policy that
        // is precisely the state that needs the user, so don't tell them everything is fine.
        var actionsText = actionLines.Count > 0
            ? string.Join("\n", actionLines)
            : stats.Conflicts > 0
                ? "(no hay acciones automáticas — cada diferencia es un conflicto que espera tu decisión)"
                : "(nada para hacer — ya está todo al día)";

        var dialog = new Window
        {
            Title = "Vista previa de la sincronización",
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
                            new Button { Content = "Sincronizar ahora", IsDefault = true, Width = 160, IsEnabled = plan.Actions.Count > 0 },
                            new Button { Content = "Cerrar", IsCancel = true, Width = 80 }
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
                ItemsSource = new[] { "Decidir después", "Conservar ambos", "Conservar mi versión local", "Conservar la versión de Proton Drive" },
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

        var applyButton = new Button { Content = "Aplicar", IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = "Cancelar", IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = conflicts.Count == 1 ? "Resolver el conflicto" : $"Resolver {conflicts.Count} conflictos",
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
                        Text = "Estos archivos cambiaron de los dos lados desde la última sincronización, así que ninguno puede ganar automáticamente. "
                             + "\"Conservar ambos\" nunca pierde nada: tu versión se renombra aparte y se sube junto a la otra.",
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
                ItemsSource = new[] { "Dejar como está", "Reintentar en la próxima sincronización", "Descartar esta acción" },
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

        var retryAllButton = new Button { Content = "Reintentar todas", Width = 160 };
        var applyButton = new Button { Content = "Aplicar", IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = "Cancelar", IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = failures.Count == 1 ? "Acción fallida" : $"{failures.Count} acciones fallidas",
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
                        Text = "Estas acciones no se pudieron completar. Reintentar vuelve a encolarlas para el próximo ciclo; "
                             + "descartar las elimina de la cola, y si la diferencia sigue existiendo el próximo análisis las "
                             + "vuelve a proponer.",
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
                Text = $"{field.Label}: {field.Value}",
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
                Content = "Copiar",
                FontSize = 11,
                Padding = new Avalonia.Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var value = field.Value;
            copyButton.Click += async (_, _) =>
            {
                await CopyToClipboardAsync(value);
                // Confirm in place: a clipboard write is otherwise completely invisible.
                copyButton.Content = "Copiado";
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(copyButton, 1);
            row.Children.Add(text);
            row.Children.Add(copyButton);
            children.Add(row);
        }

        var okButton = new Button { Content = "OK", IsDefault = true, IsCancel = true, Width = 80 };
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
            Title = "Propiedades",
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
        var yes = new Button { Content = "Continuar", Width = 100 };
        var no = new Button { Content = "Cancelar", IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = "¿Estás seguro?",
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
        var ok = new Button { Content = "OK", IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = "No se puede hacer eso",
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
        nameof(ConflictReason.BothChanged) => "Cambió acá y en Proton Drive desde la última sincronización.",
        nameof(ConflictReason.BothAppearedDiffering) => "Apareció en los dos lados con contenido distinto, sin historial compartido.",
        nameof(ConflictReason.RemoteDeletedLocalChanged) => "Se borró en Proton Drive, pero cambió acá.",
        nameof(ConflictReason.LocalDeletedRemoteChanged) => "Se borró acá, pero cambió en Proton Drive.",
        _ => "Cambios en conflicto en los dos lados.",
    };

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB"
        };
}
