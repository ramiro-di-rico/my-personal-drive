using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// Adding and editing a sync pair, with the remote folder browser as a second face of the same
/// window rather than a modal on top of a modal.
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6).
/// </summary>
public static class SyncPairDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// "Add sync pair", with the remote folder browser as a second face of the same dialog
    /// (swapping its Content) instead of a window stacked on top of it — one modal, not two.
    /// </summary>
    public static async Task<NewSyncPairRequest?> ShowAddAsync(Window owner, SyncPanelViewModel syncPanel, string remoteRootPath, SyncPairPrefill? prefill = null)
    {
        var remoteBox = new TextBox { PlaceholderText = Loc.T(StringKeys.Dialog.PairRemoteFolderPlaceholder), Width = 280, Text = prefill?.RemotePath };
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
            var folders = await TopLevel.GetTopLevel(owner)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// Lets an existing pair's direction/conflict policy change without recreating it. Remote/local
    /// paths aren't editable here — changing those already has a working path (remove, then add a
    /// new pair) and would need re-validating against every other pair, which this flow never does.
    /// </summary>
    public static async Task<EditSyncPairRequest?> ShowEditAsync(Window owner, SyncPairViewModel pair)
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

        await dialog.ShowDialog(owner);
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
    private static void ShowRemoteFolderBrowser(Window dialog, SyncPanelViewModel syncPanel, string startPath, Control formPanel, Action<string> onSelected)
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
}
