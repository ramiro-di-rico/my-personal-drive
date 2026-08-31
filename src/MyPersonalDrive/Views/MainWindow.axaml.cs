using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    }

    /// <summary>
    /// Deep paths would otherwise overflow the breadcrumb bar's fixed width with no way to see
    /// the folder you're actually in. Rather than truncating segments (which hides the middle of
    /// the path you might want to click back into), it scrolls — and always to the current
    /// folder, which is what you care about after navigating. Posted after the items collection
    /// actually changes so the ScrollViewer's Extent already reflects the new content; setting
    /// Offset past the max clamps to the real end.
    /// </summary>
    private void ScrollBreadcrumbToEnd(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => BreadcrumbScroll.Offset = new Vector(double.MaxValue, 0), DispatcherPriority.Background);

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
        if (sender is Control { DataContext: LocalNodeViewModel node } && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _localDragStartPoint = e.GetPosition(null);
            _localDragCandidate = node;
            _localDragPressedArgs = e;
        }
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

        // The row's own Button captured the pointer on PointerPressed for its own click tracking
        // (standard Avalonia ButtonBase behavior) — DoDragDropAsync needs that capture released
        // first, or it can't take over the pointer to actually track the drag.
        e.Pointer.Capture(null);

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(LocalPathsDataFormat, new[] { node.Item.Path }));
        await DragDrop.DoDragDropAsync(pressedArgs, transfer, DragDropEffects.Copy);
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
        if (!e.DataTransfer.Contains(LocalPathsDataFormat) || DataContext is not MainWindowViewModel viewModel)
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

        if (e.DataTransfer.TryGetValue(LocalPathsDataFormat) is not { Length: > 0 } localPaths)
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
        if (sender is Control { DataContext: DriveNodeViewModel node } && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _cloudDragStartPoint = e.GetPosition(null);
            _cloudDragCandidate = node;
            _cloudDragPressedArgs = e;
        }
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

        // See the matching comment in OnLocalRowPointerMoved: the row's own Button captured the
        // pointer for its click tracking, and DoDragDropAsync needs that released first.
        e.Pointer.Capture(null);

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(CloudItemsDataFormat, new[] { node.Item }));
        await DragDrop.DoDragDropAsync(pressedArgs, transfer, DragDropEffects.Copy);
    }

    private ListBoxItem? _localHighlightedDropRow;

    /// <summary>Mirrors <see cref="OnCloudListingDragOver"/> for the opposite direction — see its doc comment.</summary>
    private void OnLocalListingDragOver(object? sender, DragEventArgs e)
    {
        var listBox = sender as ListBox;
        if (!e.DataTransfer.Contains(CloudItemsDataFormat) || DataContext is not MainWindowViewModel viewModel)
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

        if (e.DataTransfer.TryGetValue(CloudItemsDataFormat) is not { Length: > 0 } items)
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
            Title = "Select proton-drive executable",
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
            Title = "Select default sync folder",
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

        viewModel.BreadcrumbItems.CollectionChanged -= ScrollBreadcrumbToEnd;
        viewModel.BreadcrumbItems.CollectionChanged += ScrollBreadcrumbToEnd;

        // ExplorerColumnsGrid.ColumnDefinitions[2] is star-sized so the splitter can resize it —
        // which also means it doesn't shrink to 0 on its own just because IsVisible on its content
        // goes false, the way an Auto column (the Status sidebar's) does. Kept in sync here instead.
        viewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        viewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        ApplyLocalExplorerPanelColumnWidth(viewModel.IsLocalExplorerPanelVisible);

        viewModel.SyncPanel.RequestNewPairAsync = () => PromptForNewPairAsync(viewModel.SyncPanel, viewModel.RootPath);
        viewModel.SyncPanel.RequestPreviewConfirmationAsync = ShowPreviewAsync;
        viewModel.SyncPanel.RequestConflictResolutionsAsync = ShowConflictsAsync;
        viewModel.SyncPanel.RequestConfirmationAsync = AskAsync;
        viewModel.SyncPanel.RequestEditPairAsync = PromptForEditPairAsync;
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
            Title = "Select files to upload",
            AllowMultiple = true
        });

        return files.Select(file => file.Path.LocalPath).ToList();
    }

    private async Task<string?> PickDownloadFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select download folder",
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
            Title = "Rename Item",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Enter new name for '{currentName}':", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Rename", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancel", IsCancel = true, Width = 80 }
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
            PlaceholderText = "New folder name",
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = "Create Folder",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "Enter name for the new folder:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Create", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancel", IsCancel = true, Width = 80 }
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
            PlaceholderText = "Leave empty to use original name",
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = "Copy Item",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Optional new name for copy of '{currentName}':", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Copy", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancel", IsCancel = true, Width = 80 }
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
            filesList += $"\n... and {conflictingFiles.Count - 10} more";
        }

        var dialog = new Window
        {
            Title = "Upload Conflict",
            Width = 450,
            Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "The following files already exist in the drive:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = filesList, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "What would you like to do?", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Vertical,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Children =
                        {
                            new Button { Content = "Keep Both (Rename new files)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.KeepBoth },
                            new Button { Content = "Replace (Overwrite existing files)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Replace },
                            new Button { Content = "Skip (Don't upload conflicting files)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Skip },
                            new Button { Content = "Cancel", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.None }
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
            Title = "Save CLI activity",
            SuggestedFileName = "cli-activity.log",
            DefaultExtension = "log"
        });

        return file?.Path.LocalPath;
    }

    /// <summary>
    /// "Add sync pair", with the remote folder browser as a second face of the same dialog
    /// (swapping its Content) instead of a window stacked on top of it — one modal, not two.
    /// </summary>
    private async Task<NewSyncPairRequest?> PromptForNewPairAsync(SyncPanelViewModel syncPanel, string remoteRootPath)
    {
        var remoteBox = new TextBox { PlaceholderText = "/my-files/Documents", Width = 280 };
        var remoteBrowseButton = new Button { Content = "Browse", IsVisible = syncPanel.GetRemoteFolderChildren is not null };
        var localBox = new TextBox { Width = 280, IsReadOnly = true, PlaceholderText = "Choose a local folder..." };
        var browseButton = new Button { Content = "Browse" };

        // RemoteToLocal stays first, and therefore the default: it's the only direction that
        // cannot destroy anything in the cloud (docs/PLAN-LOCAL-SYNC.md §15).
        var directionBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                "Download only  (remote → local)",
                "Upload only  (local → remote)",
                "Two-way  (remote ↔ local)",
            },
            SelectedIndex = 0,
        };

        var policyBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                "Ask me  (park the conflict, decide later)",
                "Keep both  (never loses either version)",
                "Prefer local",
                "Prefer remote",
            },
            SelectedIndex = 0,
        };

        var policyLabel = new TextBlock { Text = "When both sides changed:", FontWeight = Avalonia.Media.FontWeight.Bold };

        // The conflict policy is only ever consulted in two-way mode — a one-way mirror's source
        // side wins by definition, so showing the choice there would imply a decision that
        // doesn't exist.
        void SyncPolicyVisibility()
        {
            var isTwoWay = directionBox.SelectedIndex == 2;
            policyBox.IsVisible = isTwoWay;
            policyLabel.IsVisible = isTwoWay;
        }

        directionBox.SelectionChanged += (_, _) => SyncPolicyVisibility();
        SyncPolicyVisibility();

        browseButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the local folder to sync into",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                localBox.Text = folders[0].Path.LocalPath;
            }
        };

        var addButton = new Button { Content = "Add", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        var formPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "Remote folder path:", FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { remoteBox, remoteBrowseButton }
                },
                new TextBlock { Text = "Local folder:", FontWeight = Avalonia.Media.FontWeight.Bold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { localBox, browseButton }
                },
                new TextBlock { Text = "Direction:", FontWeight = Avalonia.Media.FontWeight.Bold },
                directionBox,
                policyLabel,
                policyBox,
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
            Title = "Add sync pair",
            Width = 480,
            Height = 520,
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

                result = new NewSyncPairRequest(remoteBox.Text.Trim(), localBox.Text.Trim(), direction, policy);
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
                "Download only  (remote → local)",
                "Upload only  (local → remote)",
                "Two-way  (remote ↔ local)",
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
                "Ask me  (park the conflict, decide later)",
                "Keep both  (never loses either version)",
                "Prefer local",
                "Prefer remote",
            },
            SelectedIndex = pair.ConflictPolicy switch
            {
                ConflictPolicy.KeepBoth => 1,
                ConflictPolicy.PreferLocal => 2,
                ConflictPolicy.PreferRemote => 3,
                _ => 0,
            },
        };

        var policyLabel = new TextBlock { Text = "When both sides changed:", FontWeight = Avalonia.Media.FontWeight.Bold };

        void SyncPolicyVisibility()
        {
            var isTwoWay = directionBox.SelectedIndex == 2;
            policyBox.IsVisible = isTwoWay;
            policyLabel.IsVisible = isTwoWay;
        }

        directionBox.SelectionChanged += (_, _) => SyncPolicyVisibility();
        SyncPolicyVisibility();

        var saveButton = new Button { Content = "Save", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        var dialog = new Window
        {
            Title = "Edit sync pair",
            Width = 440,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = pair.RemotePath, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = pair.LocalPath, Opacity = 0.7 },
                    new TextBlock { Text = "Direction:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    directionBox,
                    policyLabel,
                    policyBox,
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

            result = new EditSyncPairRequest(direction, policy);
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
        var upButton = new Button { Content = "⬆ Up", IsEnabled = false };
        var statusText = new TextBlock { Opacity = 0.7, IsVisible = false };
        var itemsPanel = new StackPanel { Spacing = 4 };
        var selectButton = new Button { Content = "Select this folder", IsDefault = true, Width = 160 };
        var backButton = new Button { Content = "◀ Back", IsCancel = true, Width = 90 };

        var browsePanel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "Choose a remote folder", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold },
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
            statusText.Text = "Loading...";

            try
            {
                var children = await getChildren(currentPath, CancellationToken.None);
                var folders = children
                    .Where(item => item.IsFolder)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                statusText.IsVisible = folders.Count == 0;
                statusText.Text = "(no subfolders here)";

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
                statusText.Text = $"Couldn't list this folder: {ex.Message}";
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
            $"↓ {stats.FilesToDownload} file(s) to download ({FormatBytes(stats.BytesToDownload)}), {stats.FoldersToCreateLocally} folder(s) to create locally.",
            $"↑ {stats.FilesToUpload} file(s) to upload ({FormatBytes(stats.BytesToUpload)}), {stats.FoldersToCreateRemotely} folder(s) to create remotely.",
            $"🗑 {stats.ToDeleteLocal} local item(s) to local trash, {stats.ToTrashRemote} remote item(s) to Proton's trash.",
        };

        if (stats.FilesToMoveLocally > 0)
        {
            lines.Add($"↔ {stats.FilesToMoveLocally} file(s) moved locally to follow Proton Drive — no re-download needed.");
        }

        if (stats.FilesToMoveRemotely > 0)
        {
            lines.Add($"↔ {stats.FilesToMoveRemotely} file(s) moved on Proton Drive to follow this machine — no re-upload needed.");
        }

        if (stats.Conflicts > 0)
        {
            lines.Add($"⚠ {stats.Conflicts} conflict(s) — both sides changed.");
        }

        foreach (var warning in warnings)
        {
            lines.Add($"⛔ {warning}");
        }

        var summary = string.Join("\n", lines);

        var actionLines = plan.Actions.Take(50).Select(a => $"{a.Operation}  {a.RelativePath}").ToList();
        if (plan.Actions.Count > 50)
        {
            actionLines.Add($"... and {plan.Actions.Count - 50} more");
        }

        // A plan with no actions but with conflicts isn't "up to date" — under the Ask policy that
        // is precisely the state that needs the user, so don't tell them everything is fine.
        var actionsText = actionLines.Count > 0
            ? string.Join("\n", actionLines)
            : stats.Conflicts > 0
                ? "(no automatic actions — every difference is a conflict awaiting your decision)"
                : "(nothing to do — already up to date)";

        var dialog = new Window
        {
            Title = "Sync preview",
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
                            new Button { Content = "Run now", IsDefault = true, Width = 100, IsEnabled = plan.Actions.Count > 0 },
                            new Button { Content = "Close", IsCancel = true, Width = 80 }
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
                ItemsSource = new[] { "Decide later", "Keep both", "Keep my local version", "Keep the Proton Drive version" },
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

        var applyButton = new Button { Content = "Apply", IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = conflicts.Count == 1 ? "Resolve conflict" : $"Resolve {conflicts.Count} conflicts",
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
                        Text = "Both sides of these files changed since the last sync, so neither can win automatically. "
                             + "\"Keep both\" never loses anything: your version is renamed aside and uploaded alongside.",
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
    /// A plain yes/no. "Cancel" is the default button, not "Continue": every question routed here is
    /// a warning about doing something big, so the safe answer should be the one a stray Enter picks.
    /// </summary>
    private async Task<bool> AskAsync(string question)
    {
        var yes = new Button { Content = "Continue", Width = 100 };
        var no = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = "Are you sure?",
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

    private static string DescribeReason(string? reason) => reason switch
    {
        nameof(ConflictReason.BothChanged) => "Changed here and on Proton Drive since the last sync.",
        nameof(ConflictReason.BothAppearedDiffering) => "Appeared on both sides with different content, with no shared history.",
        nameof(ConflictReason.RemoteDeletedLocalChanged) => "Deleted on Proton Drive, but changed here.",
        nameof(ConflictReason.LocalDeletedRemoteChanged) => "Deleted here, but changed on Proton Drive.",
        _ => "Conflicting changes on both sides.",
    };

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB"
        };
}
