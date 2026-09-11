using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// A click-through browser over the active provider's folder tree, returning the folder the user
/// chose (or null if they backed out). This is what "Move to..." asks the target for — the move
/// itself never leaves the provider, so the picker only ever needs listings, never a download.
///
/// The same browser exists inside <see cref="SyncPairDialog"/> as an internal page of that dialog;
/// it stays there rather than being unified with this one, because there it has to swap the
/// add-pair form back in on "Back" instead of closing a window. Only existing folders can be
/// reached here — like the sync picker, there is no "create folder" affordance.
/// </summary>
public static class RemoteFolderPickerDialog
{
    private static Localizer Loc => Localizer.Instance;

    public static async Task<string?> ShowAsync(
        Window owner,
        Func<string, CancellationToken, Task<IReadOnlyList<DriveItem>>> getChildren,
        string prompt,
        string startPath)
    {
        var currentPath = startPath;
        var pathHistory = new Stack<string>();

        var pathText = new TextBlock { FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var upButton = new Button { Content = Loc.T(StringKeys.Dialog.RemoteBrowserUp), IsEnabled = false };
        var statusText = new TextBlock { Opacity = 0.7, IsVisible = false };
        var itemsPanel = new StackPanel { Spacing = 4 };
        var selectButton = new Button { Content = Loc.T(StringKeys.Dialog.RemoteBrowserSelect), IsDefault = true, MinWidth = 160 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, MinWidth = 90 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.MoveTargetTitle),
            Width = 520,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        FontSize = 16,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { upButton, pathText },
                    },
                    new ScrollViewer { Height = 320, Content = itemsPanel },
                    statusText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { selectButton, cancelButton },
                    },
                },
            },
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

        string? result = null;
        selectButton.Click += (_, _) =>
        {
            result = currentPath;
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        dialog.Opened += (_, _) => _ = LoadAsync();

        await dialog.ShowDialog(owner);
        return result;
    }
}
